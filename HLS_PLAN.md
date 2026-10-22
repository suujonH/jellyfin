# HLS 重构计划

> 工作文档，跟随 `feature/hls` 分支。第 8 阶段写出 `HLS.md` 后删除。
> 进度见文末「进度」；第 0 阶段的测量结论见「附录：第 0 阶段结论」。

## 背景

**实时 HLS 的问题**（`DynamicHlsController` 一条链路）：
- 播放列表与 ffmpeg 的切点各算各的。播放列表在 `DynamicHlsPlaylistGenerator` 里按关键帧或等长计算，ffmpeg 只拿到 `-hls_time`。重新 seek 时还靠 `-ss` 加 0.5 秒补偿（`EncodingHelper.GetFastSeekCommandLineParameter`）。
- 每个切片 URL 都带着整套参数，外加 `runtimeTicks` 和 `actualSegmentLengthTicks`，每次请求都要重跑 `GetStreamingState`。
- 进度靠扫目录、看修改时间、判断下一段文件是否存在。
- 远程源的签名地址必须覆盖整场播放。

**预编码的问题**（DistributedHls 插件 0.2 + Worker 0.3）：
- 插件依赖服务器内部接口，靠装饰器和全局过滤器接入。
- 成果目录必须是服务器路径，放到 S3 时需要 s3fs 挂载，再由 S3Storage 插件把文件响应改成 302。
- 核心已经有网络存储（访问权限、外部地址、逐对象签名）和 TrueCache，两套机制重复。

**目标**：
- 一套 HLS 模型：实时转码（服务器本机）与预编码（Worker）共用切片计划和播放列表写法。
- 预编码成果可以放在本地目录或网络存储（S3），由 Worker 决定并上报根。档位的根设置了外部地址时，客户端直接从存储读取切片。
- 任务中心并入核心，Worker 按新的任务中心彻底重写。

## 已确认的决定

- **切片计划是唯一依据**：播放列表、ffmpeg 切点、seek 起点和成果校验都由它决定。
- **remux 但没有关键帧时**：
  - 本地文件：按需提取，有时间上限；超时则本次转码视频，提取在后台继续。
  - 远程源（没有 TrueCache）：转码视频。
- **范围**：只做点播；Live TV（`live.m3u8`）不动。
- **第 0 阶段的原型已完成**，切法见附录。
- **Worker 只做预编码**。实时转码始终由服务器本机完成，切片存在本地。
- **任务中心并入 fork 核心**，数据放进 `jellyfin.db`。插件停用，数据迁移过来。
- **Worker 自持凭据，服务器不下发任何存储配置**：Worker 维护 S3 与 SFTP 连接库，在识别时上报自己的存储根，用自己的凭据上传；服务器只给根之下的相对目录，并逐任务、逐档位、逐删除记录记住根（第四节）。
- **档位三层过滤**：阶梯 → 全局开启 → 媒体库单独选择（5.1）。Worker 添加媒体时可以随手预编码一部分档位（5.3）。
- **S3 上的媒体必须有 TrueCache 条目**，由 Worker 调用 `Jellyfin.TrueCache.Tool` 生成（第六节）。
- **直连 = 档位的根设置了外部地址**，沿用「配置外部地址 = 公开」，不另设开关。
- **Worker 彻底重写，不保留历史包袱**：不兼容旧协议，去掉 `submit`；仓库为 `suujonH/Jellyfin.HLS.Worker`。

## 一、总体结构

```text
PlaybackInfo → 源列表：原始 ／ 预编码档位（1080p…）／ 自动
  原始需要转码 → 实时 HLS：HlsSession → EncoderRun（本机 ffmpeg）→ 本地切片 → 服务器发送
  预编码档位   → 交付：授权 → 服务器生成播放列表 → 切片：存储直连 或 服务器转发

任务中心：媒体库同步 → 排程 → Worker 领取 → 识别（哈希 + 探测）→ 编码计划
          → 编码 → 用 Worker 自己的凭据上传到它的根 → 完成 → 校验 → 发布 → 删除队列
```

## 二、切片计划（两条链路共用）

位置：`src/Jellyfin.MediaEncoding.Hls/Plans/`

```csharp
public sealed record HlsSegment(int Index, long StartTicks, long DurationTicks);   // EndTicks

public sealed class SegmentPlan
{
    public SegmentPlanKind Kind { get; }            // Keyframe（remux）/ Uniform（转码、音频、预编码）
    public IReadOnlyList<HlsSegment> Segments { get; }
    public long TotalTicks { get; }
    public int TargetDurationSeconds { get; }       // 最长一段向上取整
    public long FrameTicks { get; }                 // 帧长，未知（纯音频）时为 0
    public long ToleranceTicks { get; }             // 半帧；没有帧率时 10 毫秒
    public IReadOnlyList<long> Keyframes { get; }   // 关键帧计划的全部可用关键帧，边界是其中一部分
    public int IndexAt(long ticks);
}
```

**时间轴**：计划的时间是「源 pts − 格式 start_time」，即 ffmpeg `-ss` 和 `-copyts -start_at_zero` 的时间轴。第 1 阶段核对的结果（`KeyframeTimelineTests` 用真实样本逐个比对 ffprobe）：
- **ffprobe 提取器**原来给的是绝对 pts，TS 等容器差一个 start_time（样本 1.462 秒，`-output_ts_offset 100` 时 101 秒）。现在同一次 ffprobe 读出 `format=start_time` 并减去；TrueCache 工具生成 `keyframes.json` 用的也是它。
- **mkv 提取器**的 cue 时间就是块时间戳，Matroska 文件从 0 开始，不需要换算（视频晚开始的样本也一致）。
- **MP4 提取器**（新增）按 ffmpeg mov 解复用器（`mov_fix_index`）的规则，样本的显示时间 = 解码时间 + ctts 偏移：
  - 有编辑列表的轨道从前导空编辑的结束开始（ffmpeg 直接取空编辑之和作 `start_time`），媒体编辑从那里开始呈现：音频等呈现 media_time，丢弃之前的样本（例如预滚样本：空编辑 8 秒、media_time 为 1024 个采样时，起点是 8.000）；视频则整体平移，使编辑内最早的帧落在那里，即减去「编辑内解码顺序第一个样本的解码时间」与「编辑内最早的显示时间」中较大的一个（media_time 为 0 而有 B 帧延迟时，第一个关键帧在 0 而不是 0.083）；
  - 没有编辑列表的轨道从解码顺序第一个样本的显示时间开始，可以为负，之后的样本也可能更早；
  - 结果减去最早的音视频轨道。时长与 ffprobe 相同，取最晚的音视频轨道结束减去格式起点；轨道结束 = 轨道起点 + min(mdhd 时长, stts 样本时长之和, 全部编辑之和)，编辑之和包括空编辑（空编辑 8 秒、媒体编辑 3 秒时，ffprobe 的时长是 11 秒）；
  - 编辑的时长按 ffmpeg 换算到媒体时间刻度后再比较。ffprobe 不读 `mvhd` 的影片时长（改成任意值，格式时长不变）。

  以下情况不给结果，交给 ffprobe：
  - 编辑列表不是「若干前导空编辑 + 一个速率为 1 的媒体编辑」：多个媒体编辑、速率不为 1、只有空编辑、没有条目、媒体编辑之后还有空编辑（ffmpeg 可能重放样本）、media_time 为 -1 以外的负数；
  - 视频用紧凑的 `stz2` 记录样本大小（零字节的样本 ffmpeg 不读，不算关键帧，只从 `stsz` 查）；
  - 媒体编辑之外有关键帧（ffmpeg 丢弃编辑之外的帧，ffprobe 提取器也不收带 D 标志的关键帧）；
  - 视频有 `stps`（QuickTime 的部分同步样本表，用于 MPEG-2 开放 GOP）或分组类型为 `rap `、`sync` 的 `sbgp`（如 HEVC 开放 GOP），ffmpeg 也把其中的样本当作关键帧；
  - 字幕、章节、时间码等其他轨道早于音视频开始（早不到 1 秒时 ffmpeg 以它为格式起点，但要看探测时是否读到这条轨道）；
  - 分片 MP4（有 `mvex`，样本可能一部分在 moov、其余在 moof）。
- **数据库**：旧版本存的 ffprobe 结果是绝对时间。这些数据只来自手动运行的计划任务，只有本地文件（旧链路按需提取只用元数据提取器）。迁移 `DeleteUnalignedKeyframeData` 随第 2 阶段加入，删除所有已存的关键帧，由关键帧来源按需重新读取：容器不能说明起点（MP4 的轨道也可以都晚开始），而第 1 阶段的旧链路重建不了 ffprobe 的数据，所以那时不删。MP4 的按需提取也随第 2 阶段启用（见关键帧来源第 4 项）：旧链路经 `CacheDecorator` 先查数据库，在删除之前启用，旧链路会读到这些绝对时间的数据。
- **TrueCache**：旧工具为 TS 等容器生成的 `keyframes.json` 是绝对时间，需要重新生成（第 8 阶段写进文档）。时间轴对不上时，文件仍按起点归入计划的段（附录 A.2），播放连续，只是段长与播放列表不符。

**`SegmentPlanner`**：
- 是纯函数：相同输入得出相同计划。
- 关键帧计划：边界取关键帧。因为 GOP 文件在每个关键帧都切开（见附录），任何关键帧都可以作为边界。规则：第一段从 0 开始；之后每个边界取离「上一边界 + L」最近的关键帧，但至少在上一边界 L/2 之后；流的终点也算一个候选，所以很短的余下部分并入最后一段；距离相等取后者。旧的 `ComputeSegments` 在长 GOP 之后会紧接着切出很短的段，新规则没有这个问题。
- 不用关键帧计划（改为转码视频）的情况：没有关键帧；平均间隔低于 **0.25 秒**（全帧内编码，DVB 的 0.48 秒 GOP 仍然 remux）；最长一段超过 **max(30 秒, 3L)**（客户端要取完一整段才能播放）。
- 平均间隔按不同的关键帧时间计算（其他来源的数据，例如 TrueCache 条目，可能有重复）。
- 均匀计划（转码）：段长按帧取整，`L' = ceil(L × fps) / fps`（23.976 fps、6 秒时为 144 帧、6.006 秒）；余下部分短于容差时并入最后一段。纯音频不取整。

**`HlsCuts`**（附录 A.4 的规则，纯函数）：
- `GetRunStart(plan, n, mode)` 得出 `HlsRunStart(InputSeekTicks, OutputSeekTicks, FirstFileStartTicks)`。`FirstFileStartTicks` 是 run 首个文件在计划上的起点（列表不记录），为 null 时丢弃该文件：
  - remux：`-ss` = 边界前那个关键帧 − 1 秒，首个文件丢弃；不大于 0 时不 seek，首个文件从 0 开始，保留；
  - 转码：`-ss` = 边界 − 半帧，首个文件就是第 n 段；
  - 纯音频转码：`-ss` = 边界；纯音频复制：输入 `-ss` = 边界 − 2 秒，输出 `-ss` 和 `-output_ts_offset` = 边界。
- `GetForcedKeyframeTicks(plan, start, end)`：边界 − 半帧，从 max(start, 1) 到 end（end 那个边界也包括，不超出计划）。
- `FormatSeconds`：秒，精确到微秒。
- 第 0 阶段的切点测试已改为使用 `SegmentPlanner` 和 `HlsCuts`，所以实测约束的是生产代码。

**关键帧来源**（`IKeyframeSource`），依次尝试：
1. TrueCache 条目（有声明就只用声明，不读文件）；
2. 数据库（`KeyframeRepository`）；
3. 远程源到此为止，返回 null（转码视频）；
4. 元数据提取器（mkv、新增的 MP4），只对 `AllowOnDemandMetadataBasedKeyframeExtractionForExtensions` 中的扩展名。第 2 阶段与 `DeleteUnalignedKeyframeData` 一起，在默认值中加入 mp4、m4v、mov，并加入迁移 `AddMp4KeyframeExtraction`，把仍是旧默认值（只有 mkv）的配置补上。第 1 阶段只注册 MP4 提取器，旧链路对 MP4 的行为不变；
5. 本地文件用 ffprobe 读包，等待 `EncodingOptions.KeyframeExtractionTimeoutSeconds`（默认 10，0 表示不按需读取）。超时返回 null，本次转码视频，提取在后台继续，由 `CacheDecorator` 写入数据库。同一条目只读一次（后来的请求加入正在进行的读取），同时最多读两个文件；读不出关键帧的条目在服务器重启前不再重读。

关键帧数据按条目保存，是第一条视频流的（mkv、MP4 提取器取第一条视频轨，ffprobe 用 `-select_streams V:0`，即第一条不是封面的视频流）。播放选了其他视频流时不用关键帧数据：旧链路改用等长切片，新链路转码视频。

**`HlsPlaylistWriter.WriteMedia(...)`**：由计划或实际时长写出媒体播放列表，实时和预编码共用。格式沿用现有生成器：TS 为版本 3，fMP4 为版本 7 并带 `EXT-X-MAP`；`TARGETDURATION` 取最长一段向上取整。

## 三、实时 HLS（服务器本机）

### 3.1 播放列表、切点与 seek

- `main.m3u8` 由计划写出。
- 切片 URL 改为 `hls1/{sessionId}/{n}.{ext}?{原查询}`：
  - 去掉 `runtimeTicks` 和 `actualSegmentLengthTicks`，由服务器按计划算出。
  - 旧参数仍然接受，忽略即可。
  - 保留原查询，用于在会话丢失后重建。
- **切片方式**：segment muxer 在每个关键帧切开，得到「GOP 文件」，并逐行写出 CSV 列表。计划的第 k 段 = 同一次 run 中起点落在该段范围内的 GOP 文件，按顺序拼接发送。参数和规则见附录 A。
- **转码**：在计划边界强制关键帧，用绝对时间列表，不用表达式；关闭场景切换关键帧。
- **重新开始**（seek 规则见附录 A.4）：
  - remux：从第 n 段边界之前那个关键帧再往前 1 秒 seek，run 的第一个文件丢弃。
  - 转码：精确 seek 到边界前半帧，第一个文件就是第 n 段。
  - 纯音频：从边界开始。
  - 不再使用 hls muxer 的 `-start_number` 和 0.5 秒补偿。
- 实时 HLS 的切片始终放在本地转码目录。一次 run 的文件平铺在转码目录中，都以 run id 开头（`{id}.csv`、`{id}-init.mp4`、`{id}-{n}.ts`），与其他转码相同：`TranscodeManager` 结束任务时按前缀删除。

### 3.2 会话与编码运行

位置：`src/Jellyfin.MediaEncoding.Hls/Sessions/`

```csharp
sealed class HlsSession        // 一次播放中的一个变体
{
    string Key;                   // 用户、条目和切片 URL 的查询（去掉旧参数，按名称排序）
    string Id;                    // Key 的 MD5，出现在切片 URL 中；重建的会话 id 不变
    SegmentPlan Plan;
    HlsCutMode Mode;              // 复制或转码视频、复制或转码音频，整个会话不变
    HlsEncoderRun? Run;
    string InitPath;              // fMP4：第一次取得的 init，整个会话只发送这一个
    SemaphoreSlim Lock;
    bool IsClosed;                // 闲置 30 分钟后关闭；之后又来的请求把它加回去
}

sealed class HlsEncoderRun     // 一次 ffmpeg
{
    string Id;                    // 文件名前缀
    int StartIndex;
    int EndIndex;                 // 转码时一次 run 最多 600 段（附录 A.4）
    TranscodingJob Job;           // 复用 TranscodeManager 的进程管理、进度上报和 Kill
    Task<HlsSegmentLocation> WaitForAsync(int index, CancellationToken ct);
    Task<bool> WaitForInitAsync(CancellationToken ct);
}
```

**会话的创建与重建**：
- `main.m3u8` 按会话键创建或取得会话。不知道关键帧而改为转码视频时，切片 URL 的查询加上 `AllowVideoStreamCopy=false`，重建时得到同样的决定。`master.m3u8` 先做同样的判断：变体 URL 带上 `AllowVideoStreamCopy=false`，CODECS 等属性按转码后的流写出。客户端没有指定视频编码时同时带上 `VideoCodec=h264`：没有编码时 ffmpeg 复制视频，切法也按复制判断。
- 切片请求用同样的键查找会话。找不到时（例如服务器重启后）从查询重建，计划是确定的，所以结果与原来相同。

**切片请求的流程**：
1. 取得会话锁。
2. 第 n 段已完成且文件存在：直接发送（拼接该段的 GOP 文件）。
3. 以下情况结束旧 run（等它正在发送的请求结束），从计划中第 n 段开始一次新 run，并**重新解析源地址**（`INetworkStorageManager.ResolveShortcutTarget`）：没有 run；run 失败或已停止；n 在 run 的起点之前；n 在 run 的终点之后；n 已完成但文件已被清理；n 比已完成位置超前约 24 秒以上。开始新 run 时，同一播放（PlaySessionId，没有时按 DeviceId）的其他变体的 run 停止。
4. 等待第 n 段完成。读取器增量读取 `list.csv`，按附录 A.2 的规则判断完成，由文件变化事件唤醒，不轮询。run 异常退出时返回错误。会话中途不改为转码：客户端已经取得复制的 init，转码后的片段与之不符。
5. 发送完成后释放请求计数，记录已发送位置和该段的终点，供节流和清理使用。

**结束会话**：入口不变，仍是 ping 超时、`DELETE /Videos/ActiveEncodings` 和停止播放。

**归档存储**：创建会话时拒绝，返回 403，沿用 `StreamingHelpers` 中的现有检查。

### 3.3 节流、清理与源地址

- `TranscodingThrottler` 改为按段序号：已完成位置超出已发送位置约 `ThrottleDelaySeconds` 时暂停 run。
- `TranscodingSegmentCleaner` 改为按段序号：删除已发送位置之前、超出 `SegmentKeepSeconds` 的 GOP 文件。
- 每次 run 都重新签发源地址；复用已有任务的媒体源时也重新解析。但复制和纯音频的 run 不截断，可以一直持续到播放结束，所以 S3.md 中「有效期要长于最长一次播放」的要求不变，只补充重新开始时会换新地址。

## 四、HLS 存储（预编码成果）

**服务器没有 HLS 存储设置**。Worker 自持凭据，在识别（和预编码）时上报自己的存储根 `Base`，形式与原来的 `Store` 相同：
- 本地目录：服务器上的绝对路径（Worker 经 SFTP 写入）；
- 网络存储：`s3://桶/前缀`，前缀可以为空（整个桶）。只支持 s3。

根之下的相对目录仍由服务器在计划中给出，Worker 把根拼在前面后用自己的凭据上传。根逐任务（`HlsJobs.Base`）、逐档位（`HlsRenditions.Base`）、逐删除记录（`HlsDeletions.Base`）保存；多个根并存，同一内容的不同档位允许来自不同的根。

**根的校验**（`Identify`、`Jobs/Eager` 和 `Bases/Check` 共用同一个实现，结果为 `HlsStoreTestResult`）：
1. 能由 `HlsStoreFactory` 解析（路径规则见下）；
2. s3：服务器有该桶的访问权限，且不是归档（归档的存储客户端无法播放）；
3. 本地目录：绝对路径、目录存在，与任何媒体库路径互不包含（在其下或包含它都不行，删除档位目录时可能带走媒体文件），也不在服务器的程序数据目录或数据目录之下；
4. 根可信，与监视同一判断：有标记，或该根下还没有已发布的目录，或最近发布的样本至少有一个还在；列出失败也算不通过。

不通过时 `Identify`、`Jobs/Eager` 回 `400` 带原因，`Bases/Check` 返回失败及原因。服务器只要求该桶有一条访问权限：没有时由 Worker 用自己的凭据创建，服务器不参与。

**存储抽象**：`IHlsStore`（`MediaBrowser.Controller/Hls/`）只剩读取与删除，按根由 `HlsStoreFactory.Create(根)` 打开，可按根缓存；实现 `LocalHlsStore`、`NetworkHlsStore` 在 `Jellyfin.Server.Implementations/Hls/`。路径相对于根目录，用 `/` 分隔，不允许空段、以点或空格结尾的段（Windows 会去掉它们，`...` 就成了上级目录）、`\`、`:` 和控制字符（列出网络存储时 XML 无法携带）；本地目录还核对完整路径确实在根目录之下。
- `CheckRootAsync(已发布目录样本)`：根目录不可信时返回原因（见下文的根目录标记）。网络存储还要求有访问权限且不是归档。
- `ListAsync(目录)`：目录下的全部文件（含子目录），路径相对于该目录，带大小和修改时间；目录不存在时为空。
- `DeleteDirectoryAsync(目录)`：删除目录及其文件；本地目录还删除因此变空的上级目录，直到根目录。
- `GetServerPath`、`GetClientAddress`：见下表。
- 读取和删除失败统一为 `IOException`。
- **删除**：`WriteAsync`、`GetUploadAddress`、`TestAsync`，以及两个实现中的写入、写标记、写测试对象逻辑。

**布局**沿用插件，迁移来的成果因此不需要移动：`{媒体}/{video|audio|subtitles|preview}/{序号}/{租约令牌}/`。目录名含租约令牌，一个租约只属于一个任务、一个根，所以 `HlsDeletions` 仍以 `RelativeDirectory` 为主键。

**根目录标记**：`.jellyfin-hls`，也接受旧名 `.distributed-hls`。**服务器不再写标记**，标记由 Worker 在配置或测试目标时写入。规则：
- 根目录不存在或读不出来：不可信。
- 有标记：可信。
- 该根下没有已发布的成果：可信。
- 没有标记、已发布目录的样本至少有一个在：可信（不写标记）；一个都不在：不可信。
- 不可信时，只有记在这个根上的记录暂停校验和删除，不消耗重试次数；其他根照常。
- 网络存储同样适用，防止访问权限被改到别的桶后，系统误以为成果全部丢失、整库重新编码。

**按根监视**（`HlsRootMonitor`）：取已发布档位、校验中的任务和删除队列中出现的全部根，逐个抽样核对（样本取该根下的已发布目录）。`HlsStatus.StoreProblems` 列出仍在使用、有问题的根及原因。

| 操作 | 本地目录 | 网络存储 |
| --- | --- | --- |
| Worker 上传 | Worker 经 SFTP 写入，服务器不参与 | Worker 用自己的 S3 凭据写入，服务器不参与 |
| 校验 | 逐个 stat | 按档位目录 `ListObjectsV2`，核对存在与大小 |
| 删除 | 删目录 | 列出前缀后批量 `DeleteObjects`，服务不支持时逐个 `DELETE` |
| 服务器读取（转发） | `PhysicalFile` | 按网络存储的规则读取：公开读外部地址，否则读 Endpoint（`ResolveShortcutTarget`） |
| 客户端地址 | 无 | 外部地址，逐对象签名，按小时对齐；不公开或归档时没有 |

完成核对、校验、删除分别用任务、档位、删除记录上的根打开存储。

**网络存储的扩展**：
- `INetworkStorageManager` 保留以下方法，都以 `s3://桶/键` 为参数：
  - `GetClientAddress(target)`：外部地址；不公开、归档或没有访问权限时返回 null；
  - `GetServerAddress(target, method)`：访问权限的 Endpoint，按方法签名，用于检查等维护操作（公开时也不用外部地址）；
  - `ListAsync(prefix, limit)`：按键的顺序；
  - `DeleteAsync(targets)`：按桶分组；不存在的对象视为已删除。
  - 列出和删除时没有访问权限、存储拒绝或无法连接，都抛出 `IOException`；目标不是可写的存储时抛出 `ArgumentException`。
- `IObjectStorageProtocol : INetworkStorageProtocol`（列出、删除），只有 `S3StorageProtocol` 实现，复用 `S3UrlSigner.GetUrl(..., method, parameters)`：
  - ListObjectsV2 的 XML 解析（`S3Xml`，按元素的本地名称匹配，不要求命名空间），按 `NextContinuationToken` 分页；截断却没有令牌时报错，不会反复读同一页。
  - DeleteObjects 每次至多 1000 个键，quiet 模式，带 `Content-MD5`；结果中 `NoSuchKey` 以外的错误抛出异常。服务不接受（400、401、403、405、501，例如不支持预签名 POST 的服务）时，本次调用的剩余对象改为逐个 `DELETE`，404 视为已删除。
- **删除** PUT 预签名整条链：`INetworkStorageManager.GetUploadAddress`、`IObjectStorageProtocol` 与 `S3StorageProtocol` 中的上传地址。访问权限测试不走这条链（已核对）。

## 五、任务中心（并入核心）

### 5.1 数据（`jellyfin.db`，EF Core 实体加迁移 `AddHlsTaskCenter`、`AddHlsBase`）

| 表 | 主要字段 |
| --- | --- |
| `HlsSources` | ItemId（主键）、LibraryId、Path（文件路径或 `.strm` 目标）、Length、DateModified、Sha256 |
| `HlsMedia` | Sha256（主键）、MediaKey（哈希前 32 位，唯一）、LibraryId、Probe（JSON）、DateOrphaned、DateCreated |
| `HlsRenditions` | (Sha256, Key)、State（Missing/Published/Failed）、JobId、PublishedJobId、RelativeDirectory、**Base**、Summary、Profile、DatePublished |
| `HlsJobs` | Id、Kind（Source/Media）、WorkKey（唯一：`source:{ItemId}` 或 `media:{Sha256}`）、ItemId、Sha256、**Base**（识别时记下）、Input（JSON，领取时选定）、Plan（JSON，识别时生成）、Status、Worker、LeaseToken、LeaseExpires、Attempt、Error、DateCreated、DateModified |
| `HlsJobOutputs` | (JobId, Key)、RelativeDirectory、Summary、Files、Segments（各段秒数） |
| `HlsDeletions` | RelativeDirectory（主键）、**Base**、NotBefore、Attempts、Reason、LastError |
| `HlsVersionItems` | (ItemId, Quality)、VersionId、Signature |

- 迁移 `AddHlsBase` 给三张表加 `Base` 列（非空，默认空串）。第 4 阶段没有发布过任何版本，空串的记录不会出现在实际数据中；读到空串的根按不可信处理。
- `HlsPublishedRendition`（发布索引）与 `HlsVerifyingJob` 带上根。
- 关联只存数据库，现有七张表够用：重新扫描后通过 SHA-256 重新接上成果。多分辨率、多音轨已在模型中。
- 各段时长只存在 `HlsJobOutputs`：已发布的档位经 `PublishedJobId` 找到它的清单，与插件读取文件清单的方式相同。一部长片约 1800 段，所以发布索引（内存）不带各段时长，播放列表按请求从数据库读取。
- 表之间不设外键，与插件相同：任务被重建时换新 id，旧任务的已发布清单仍按旧 id 保留；删除来源、媒体时由 `HlsJobStore` 自己清理。
- 时间都是 UTC 的 `DateTime`；JSON 列用 System.Text.Json 的默认设置（PascalCase、数字枚举），探测结果与插件的 JSON 相同，第 8 阶段可以原样导入。

**写入**：所有写操作都经过 `HlsJobStore`（`Jellyfin.Server.Implementations/Hls/`），在进程内的锁下以一个事务执行。任务中心是这些表唯一的写入者，所以替代了插件的 IMMEDIATE 事务。读取不加锁。

**状态机**：以插件的为基础，租约、心跳、重试、取消、校验、发布和删除队列的规则不变；探测和编码合并为一种任务（见 5.2）。插件的 `JobStoreTests` 移植过来，并按新流程改写。

**设置**：放在命名配置 `hls`（`HlsOptions`）：
- `Enabled`、`Libraries`（启用哪些库，语义不变）、`Qualities`（全局开启的档位）；
- `LibraryQualities`：媒体库 id 到档位数组的列表（`{LibraryId, Qualities}`）；
- `AllowOriginal`（全局允许原片）、`AllowLocalOriginal`（见 7.1）；
- 租约、重试、孤儿保留、播放授权有效期和签名密钥。
- **删除** `Store` 和 `OriginalUsers`。用户是否可用原片改由 `UserPolicy.EnableOriginalPlayback` 决定，默认关闭。

**档位三层过滤**：
1. 代码阶梯 `HlsEncodingLadder.Heights` 是支持范围；
2. `Qualities` 是全局开启的档位；
3. 媒体库在 `LibraryQualities` 中有一项时，它的生效档位 = 该项 ∩ 全局；没有时 = 全局。未启用的媒体库生效档位为空，它在 `LibraryQualities` 中的项被忽略。

由 `HlsSettingsSnapshot.GetQualities(libraryId)` 给出生效档位。**一个内容应有的档位** = 它在启用媒体库中的所有来源（按 `HlsSources.LibraryId`）的生效档位的并集：
- 没有这样的来源时，取 `HlsMedia.LibraryId` 的生效档位（预编码创建的内容在扫描入库前就是这样）；
- 仍为空时不同步档位，已有的保持不变，由孤儿规则处理（停用媒体库不会立即撤下成果）。
- 并集缩小时按现有规则撤下多余档位（删除记录与目录）。
- 这条解析替换三处对全局 `Qualities` 的直接使用：`HlsCoordinator` 调用 `SyncRenditionsAsync`、`HlsTaskCenter` 构造 `HlsIdentifyRequest`、`HlsJobStore.Jobs` 的 `GetDesired`。解析在 `HlsJobStore` 的事务内进行（需要读该内容的来源），参数从档位数组改为 `Func<Guid, IReadOnlyCollection<int>>`。
- 全局 `Qualities`、`LibraryQualities` 或 `Libraries` 变化时，与原来 `Qualities` 变化一样触发全量同步（内容的档位来自它所在的媒体库，选择别的媒体库也会改变它）。

**校验**（`IValidatingConfiguration`，读取时再校验一次：手改的无效设置按关闭处理，并记错误日志）：
- `LibraryQualities` 中媒体库 id 不为空、不重复；每项的档位在阶梯内且不重复；
- 每项与全局的交集不得为空，为空时拒绝保存，原因中指出该媒体库；未选中的媒体库不检查这一条（它不编码，项保留到选中时）。

**`HlsSettingsSnapshot`** 去掉 `Store`、`UploadLifetime`、`UploadGrace`。依赖它们的位置一并改：`HlsSettings` 打开存储、`HlsCoordinator` 的 `AppliedSettings.Store` 与存储变更告警（删除）、`HlsTaskCenter` 四处、`HlsVerificationService` 两处、`HlsDeletionService`、`HlsRootMonitor`、`HlsController` 的默认位置。存储一律按记录上的根打开。

**纯逻辑**（`src/Jellyfin.MediaEncoding.Hls/Published/`，移植插件）：探测解析、阶梯、档位、目录布局、完成校验。DTO 在 `MediaBrowser.Model/Hls/`，Worker API 与任务中心共用；插件的类型名加 `Hls` 前缀（`HlsProbe`、`HlsRenditionSummary` 等），字段不变。

### 5.2 流程

1. **同步**：后台循环，每 5 秒一次，并跟随媒体库事件。
   - 选定媒体库中的视频：本地文件，以及目标能解析的 `.strm`（s3、http(s)；归档存储也包括在内，预编码正好让归档原件也能播放）。
   - 记录路径或目标、大小、修改时间。`.strm` 的大小用 TrueCache 的 `MediaSize`，修改时间用 `.strm` 文件的。
   - 只取 `VideoType` 为 `VideoFile` 的条目（光盘目录等不预编码）；`.strm` 的目标须由网络存储解析为 http(s) 地址；大小只取 TrueCache 条目声明的，没有时跳过（条目的 `Size` 这时是文本文件本身的大小）。所以 S3 上的媒体必须有 TrueCache 条目。
   - 文件夹属于多个媒体库时，任意一个被选中即可，`LibraryId` 记第一个启用的媒体库，生效档位按它计算。
   - 检查或记录某个条目时出错，保留它原有的记录，不当作已删除，下次全量排程（每 10 分钟）时重试；同步某个内容的档位出错时同样重试，因为全量排程只能找到已有的档位。
   - 媒体库事件从任务中心创建时就开始记录，启动时的首次全量同步之前发生的变化也不会丢失。
   - 发布索引某次刷新失败时，记下没有读到的条目和内容，下次刷新一并读取；任务中心每 5 秒请求一次，所以提交之后的刷新失败不会让播放长期看不到变化。
2. **排程**：
   - 来源还没有哈希（新文件或指纹变化）：建任务 `source:{ItemId}`。
   - 哈希已知但有缺失的档位（新增档位、编码配置变化、校验失败）：建任务 `media:{Sha256}`，输入取该内容的任意一个来源，优先原条目。
3. **领取与识别**：Worker 领取任务，取得输入，算 SHA-256，运行 ffprobe，然后带上自己的根调用 `Identify`。任务中心：
   - 校验根（第四节），不通过回 `400`；通过时记在任务上；
   - 记下来源的哈希，按哈希保存探测结果，相同内容共享成果；
   - 由探测结果和该内容应有的档位（5.1 的三层过滤）得出应有的档位（沿用插件的阶梯规则）；
   - 缺失的档位没有其他任务负责时，本任务接手，返回编码计划，包括：
     - 每个档位的参数；
     - 切片计划：切片长度默认 6 秒，关键帧间隔 2 秒，以及预期段数，都计入编码配置版本；
     - 允许的文件名：`init.mp4`、`segment-000000.m4s` 起的切片（段数允许比计划多 1）、`subtitle.vtt`、`preview.jpg`；
   - 否则返回 `Done`，任务完成，也就是在这里去重；
   - `media:` 任务的输入哈希与预期不同时，说明来源已变化：回 409，并重新同步这个来源。
4. **编码与上传**：
   - Worker 每编完一个档位，就用自己的凭据上传到 `根/计划给出的目录`，服务器不签发地址、不接收文件。
   - 目录名含租约令牌，Worker 不会覆盖已发布的成果。
5. **完成**（202）：服务器用任务上的根打开存储并核对：
   - 档位和文件是否在计划之内；
   - 段号是否从 0 连续；
   - 段数与计划相差是否不超过 1；
   - 末段以外每段的时长，与计划相差是否不超过一帧（视频）或一个 AAC 帧（音频）。附录 B 的测量支持这条规则。

   上报的各段时长会保存下来，用来生成播放列表。
6. **校验**：用任务上的根打开存储。本地目录逐个 stat；网络存储按档位目录 List，核对大小。该根不可信时暂停。
   - 有待校验的任务时才检查它们的根：网络存储的每次检查都要发请求。
   - 某个档位核对失败时再检查一次根目录，仍然可信才判定校验失败，否则任务留在校验中。
   - 「校验已发布成果」用每个档位自己的根，对每一个核对失败的档位都先检查它的根，不可信时跳过这个根上的其余档位，已撤下的档位照常刷新索引并排程。
7. **发布**：在一个事务里把档位切换到新目录和新根。被替换的目录（连同旧根）在宽限期后删除，宽限期取播放授权有效期与旧根的外部地址有效期中较长的一个（签名地址按小时对齐，所以是设置的有效期加一小时；公开桶的地址不过期，只按授权有效期）。
8. **删除队列**：用删除记录上的根打开存储。失败按 1、2、4 分钟……退避重试，最长 6 小时，不阻塞派发。
   - 删除之后该根仍然可信，才把这一批移出队列：从失去挂载的根目录「删除」的目录可能根本不在那里。
   - 路径或根不合法的记录记日志后移出队列，不删除任何东西。
   - 已知限制：某个根上最后一个已发布的内容成为孤儿、而它的目录删除之前挂载恰好失效时，没有样本可核对，空的挂载点会被当作新存储，这批记录移出队列，文件留在原存储上。只会遗留文件，不会误删或重新编码；Worker 写了标记的根不受影响。
9. **租约**：心跳续期。过期算一次失败。过期或 Worker 报告失败时，计划中的目录在**一个租约长度**之后删除（失去联系的 Worker 可能仍在上传）；任务还没有识别、没有根时无目录可删。
10. **孤儿**：没有任何来源引用的内容，超过保留时间后删除它的成果。

### 5.3 任务的状态与识别

在插件的状态机上合并探测和编码：

- **来源任务**（`source:{ItemId}`）：来源没有哈希时（新文件、指纹变化）在写入来源的同一事务里创建；每 10 分钟的全量排程也为所有没有哈希的来源补建，以免来源在任务运行期间变化后无人接手。
  - 已完成、或已取消且没有租约的任务换新 id 重建（它可能发布过成果，清单按任务 id 保存）；等待、运行、校验中的不动；失败的等重建工作列表。
  - 「持有租约」指 `LeaseExpires` 有值：提交成果时租约结束（令牌保留），所以被取消的校验中任务可以重建。
- **媒体任务**（`media:{Sha256}`）：哈希已知但有需要任务的档位时创建，规则同插件的编码任务（等待中的沿用，进行中的不动，其余换新 id 重建），并把这些档位记到任务上。该内容没有在启用的媒体库中的来源时不创建，否则领取时立即取消、下次排程又重建。
- **领取**：取最早的等待任务，最多 20 轮，不合格的取消后继续：
  - 来源任务需要它的来源仍在启用的媒体库中；
  - 媒体任务需要一个有该哈希、在启用的媒体库中的来源（优先原条目），且至少持有一个档位。没有来源的媒体任务直接取消，替代插件的 R39 和定向领取。
  - 领取时记下输入（`Input`：条目、路径或目标、长度、修改时间），清空 `Plan` 和 `Base`，租约令牌即本次的目录名。
- **识别**（一个事务）：
  1. 租约无效：409。已经识别过、哈希和根都相同时原样返回计划（Worker 重试），哈希或根不同是 400。
  2. 根不通过校验（第四节）：400，任务不变，不计尝试（校验在事务之外先做，结果随请求传入）。
  3. 输入与来源当前的记录不同（路径、长度、修改时间）：任务回到等待，清空输入，不计这次尝试。来源已删除时，来源任务取消；媒体任务回到等待，下次领取改用该内容的其他来源，没有则取消。回 409。
  4. 媒体任务算出的哈希与任务的不同：清除该来源的哈希并为它建来源任务，媒体任务回到等待。回 409。
  5. 记下来源的哈希；新哈希保存探测结果，已知哈希沿用已保存的（R44），不同时记日志。来源任务在两次尝试之间文件变成了另一内容时，先释放它持有的旧内容的档位，否则旧内容要等它、并随它一起失败。
  6. 按已保存的探测结果和该内容应有的档位同步档位（被取消的档位删除目录）。
  7. 来源不在启用的媒体库中：任务完成，返回 `Done`。
  8. 接手所有需要任务的档位；计划 = 本任务持有、仍然需要的档位，按阶梯顺序。为空时任务完成，返回 `Done`（去重）；否则记下根，保存并返回计划。
- **计划**：哈希、`MediaKey`、编码配置版本、已保存的探测结果、切片长度 6 秒、关键帧间隔 2 秒、预期段数 `ceil(时长 / 6)`（至少 1），以及每个档位的键、种类、目录、画面尺寸与帧率、源轨道、声道。
- **失败后回到等待**：清空 `Input`、`Plan` 和 `Base`，档位仍记在任务上，下次领取重新识别（输入可能换了来源，Worker 可能换了根）。其余（租约过期、Worker 报告失败、校验失败、永久失败、重建）同插件。
- **完成时释放档位**：任务完成（识别返回 `Done`、发布）时，仍记在它上面的档位一并释放，否则这些档位不会再被排程。
- **成果在提交时核对**：`SubmitOutputsAsync` 按数据库中的计划运行完成校验，目录只取自计划，根只取自任务。

**预编码任务**（`POST /HlsWorker/Jobs/Eager`）：Worker 添加媒体时可以随手编一部分档位，没编的部分在扫描入库后由正常流程补齐。请求 `{Worker, LibraryId, Sha256, ProbeJson, Base, Qualities}`，规则依次为：
1. 媒体库不存在（不在 `VirtualFolders` 中）或未启用、哈希或探测结果无效、根不通过校验：`400`。
2. `Qualities` 为空，或不是该库生效档位的子集：`400`。
3. 该内容已存在时沿用已保存的探测结果（R44）。该内容应有的档位已全部发布：`200 {Done:true}`。
4. 该内容的 `media:` 任务处于等待、运行或校验中（或已取消但 Worker 仍持有租约），或它的任何档位由任务持有：`409`。
5. 内容不存在时创建 `HlsMedia`，写入 `LibraryId`，`DateOrphaned` 设为当前时间；扫描后来源出现时按孤儿规则清空。已存在的内容不改这两项。
6. 同步档位；计划只含请求的画质档位，音轨、文本字幕和预览图总是包含（完成校验要求上报计划中的每个档位）。没有需要任务的档位时 `200 {Done:true}`。
7. 任务为 `media:` 类型（WorkKey 规则同媒体任务，已完成或已取消的换新 id），`ItemId` 与 `Input` 为空，记下根和计划，创建即租给该 Worker（状态为运行，租约令牌即目录名）。返回 `200 {Lease, Plan}`。

已知限制：预编码进另一个媒体库的内容，在该库的文件扫描入库之前只在这一次请求中计入该库的档位；编码期间若有同步，该库独有的档位会被撤下，成果在发布时丢弃。文件入库后由正常流程重新编码，只浪费一次编码。

之后心跳、完成、校验、发布与普通任务完全一致。这个任务没有输入：`Jobs/{id}/Input` 回 `404`；失败或租约过期后回到等待，领取时没有来源即取消，与普通媒体任务相同。

### 5.4 与插件相比的变化

| 方面 | 插件 | 核心 |
| --- | --- | --- |
| 数据 | 独立的 `jobs.db` | `jellyfin.db` |
| 任务 | 探测、编码两种任务，加定向领取 | 一种任务：领取 → 识别 → 计划 → 编码 → 上传；识别时去重；另有添加媒体时的预编码任务 |
| 成果位置 | 服务器路径，放 S3 时要挂 s3fs | Worker 上报的根（本地目录或 S3），多根并存，不需要挂载 |
| 上传 | Worker 自带映射和凭据 | Worker 自持凭据，上传到自己上报的根，服务器只给相对目录 |
| 档位 | 全局 | 阶梯 ∩ 全局 ∩ 媒体库 |
| 来源 | 只有本地文件 | 本地文件和 `.strm` |
| 播放列表 | Worker 生成，服务器原样发送 | 服务器按切片计划和上报的时长生成 |
| 交付到存储 | S3Storage 插件 302，Worker 改写播放列表 | 媒体播放列表中直接写外部地址 |
| 播放接入 | 装饰器加全局过滤器 | 直接改 `MediaSourceManager`、`MediaInfoHelper` 和流路由 |

## 六、Worker 1.0（重写）

仓库 `suujonH/Jellyfin.HLS.Worker`，按核心任务中心重新设计。

**不再保留的旧功能**：协议 2；`submit`（库外文件，由「添加媒体」取代）；探测任务和定向领取；Scp 上传（换为 SSH.NET 的 SFTP）。保留 S3 与 Local 上传和 AWSSDK.S3。

**结构**：
- **引擎**做成库：服务器客户端、连接库、任务执行与心跳、输入缓存、识别、编码、上传、添加媒体、本地数据库；
- **GUI 宿主**用 Avalonia；
- **无头 `run` 宿主**：只跑引擎，供服务器部署（systemd）。

**连接库**：Worker 自持凭据（管理员 API Key），维护 S3 与 SFTP 两类连接。
- 媒体库及其服务器路径取自 `GET /Library/VirtualFolders`。每个媒体库一条默认连接，添加文件时可改选。
- SFTP 连接除主机、凭据和远端根目录外，还要填「该根目录在服务器上的路径」：媒体库目录由此映射，HLS 根放在 SFTP 时也由此得出上报的服务器路径。SFTP 用户必须与 Jellyfin 同用户或同组（0.3 README 中 umask 的问题）。
- 媒体对象放 S3 时，服务器上没有该桶的访问权限，Worker 用同一凭据调 `POST /NetworkStorage/Accesses` 建一条。
- **HLS 目标**是每个 Worker 一条全局设置，选连接库中任意一条加前缀或路径，即上报的 `Base`；不按媒体库、不按任务选。配置或测试时 Worker 在该根写入标记 `.jellyfin-hls`。「测试」做两件事：用自己的凭据写删一个测试对象，再调 `Bases/Check`。

**添加媒体**：
1. 选文件或文件夹、媒体库和连接，保留相对于所选媒体库路径的目录结构；`GET /HlsWorker/Libraries` 取得该库的生效档位；
2. 可选择不编、编一部分或全编，编的部分经 `Jobs/Eager` 提交，按普通任务的编码、上传、完成流程走；
3. 媒体对象上 S3 时，对象上传到 `{前缀}/{相对路径}/{文件名}`，上传时顺便算 SHA-256；`.strm`、`__true_cache__` 条目和外挂文件经 SFTP 写进媒体库目录，顺序是外挂文件、条目目录、最后 `.strm`（`.strm` 出现时扫描即可读到条目）；
   `.strm` 写在 `{库路径}/{相对路径}/{文件名去掉扩展名}.strm`（与视频同名的外挂字幕和图片因此归属于它），条目在旁边的 `__true_cache__/{文件名去掉扩展名}.strm/`，经 SFTP 先写 `.` 开头的临时目录再改名，与工具的做法相同；两个文件会得到同一个名字时，在写入任何东西之前拒绝；
4. 媒体直传 SFTP 时只能进媒体库目录，以临时名传入再改名，不生成 TrueCache；
5. TrueCache 条目由 Worker 调用 `Jellyfin.TrueCache.Tool` 子进程生成，不在 Worker 里重写提取逻辑（产物必须与服务器逐项一致）；工具随 Worker 发布为自包含程序。工具新增一个模式：按本地文件生成、条目名为 `.strm`、`source.size` 取本地文件大小、不写 `source.modified`（在 jellyfin 仓库实现）；trickplay、章节等设置取自 `GET /System/Configuration`。`--server-data` 读库签名下载 `.strm` 目标的能力保留，供迁移已有 S3 内容。
6. 生成到本地临时目录与经连接落地分成两步，落地走连接抽象，为今后把 `__true_cache__` 移到 S3 预留；
7. 一批结束后调 `POST /Items/{媒体库 ItemId}/Refresh` 触发扫描。

**本地数据库**（SQLite）：只记「添加媒体」添加过的媒体：本地路径、目标、大小、SHA-256。目标对 S3 媒体是 `s3://` 地址，对 SFTP 媒体是服务器路径。领到任务时按来源的目标加大小命中（不看修改时间：`.strm` 的修改时间是文本文件的），命中就直接读本地文件（本地文件已移动或修改时改为下载）并沿用记下的 SHA-256；没命中才经 `Input` 下载到输入缓存，下载的输入不记入数据库。

**调度**：处理列表（上传、TrueCache、随添加的预编码）优先；列表为空且有空闲名额时才 Poll。这是 Worker 侧规则，服务器不改。

**协议**：所有请求带 API Key（不接受用户会话）和请求头 `X-Jellyfin-Hls-Protocol: 1`。

| 方法与路径 | 请求 | 应答 |
| --- | --- | --- |
| `GET /HlsWorker/Protocol` | — | `{"Version":1}` |
| `GET /HlsWorker/Libraries` | — | 启用的媒体库：`[{Id, Name, Qualities}]`，`Qualities` 为生效档位（路径取自 `VirtualFolders`） |
| `POST /HlsWorker/Bases/Check` | `{Base}` | `HlsStoreTestResult`，校验同第四节 |
| `POST /HlsWorker/Poll` | `{Worker}` | `200 {Lease, Source, InputUrl}`；没有任务时 `204` |
| `POST /HlsWorker/Jobs/Eager` | `{Worker, LibraryId, Sha256, ProbeJson, Base, Qualities}` | `200 {Lease, Plan}` 或 `200 {Done:true}`；规则见 5.3 |
| `GET /HlsWorker/Jobs/{id}/Input?leaseToken=` | — | 本地文件返回文件流，支持 Range，文件不存在时 `404`；`.strm` 返回 302，指向服务器当场签发的存储地址；预编码任务 `404` |
| `POST /HlsWorker/Jobs/{id}/Heartbeat` | `{LeaseToken}` | `204`；租约失效时 `409` |
| `POST /HlsWorker/Jobs/{id}/Identify` | `{LeaseToken, Sha256, ProbeJson, Base}` | `200 {Plan}` 或 `200 {Done:true}`；根不通过 `400`；来源已变化或租约失效时 `409` |
| `POST /HlsWorker/Jobs/{id}/Complete` | `{LeaseToken, Renditions[{Key, Summary, Files[{Path, Length}], SegmentDurations}]}` | `202` 或 `409` |
| `POST /HlsWorker/Jobs/{id}/Failed` | `{LeaseToken, Error}` | `204` 或 `409` |

- **删除**：`Uploads`、`Files/{key}/{file}`、两条 `UploadCheck` 路由；`IHlsTaskCenter` 的上传地址、本地写入、两个上传测试方法；DTO `HlsUploadAddress`、`HlsUploadRequest`、`HlsUploadResponse`、`HlsUploadTarget`、`HlsUploadCheck`；`HlsStorePaths` 的测试对象前缀与两个方法，以及删除服务里的对应分支。对应测试一并处理。
- **认证**：`HlsWorkerProtocolAttribute` 在管理员策略之后检查：用户会话 `403`，协议请求头缺失或不符 `400`。
- **错误**：请求不合理（档位或文件不在计划中、哈希或探测结果无效、根不通过等）为 `400`，租约失效或内容有进行中的任务为 `409`，都带原因。
- **地址**：`InputUrl` 是相对于服务器地址的路径，反向代理的路径前缀因此保留。
- **完成时不检查根**：根在 `Identify` 时已校验；之后根不可信时，任务停在校验中，等根恢复后再校验（见第五节），`Complete` 照常回 `202`。

**一个任务的流程**：
1. **领取**：得到租约和来源（ItemId、路径或目标、长度、修改时间）。
   - 每隔租约的三分之一发一次心跳。
   - `409` 立即停止；心跳连续失败、租约将尽时也停止，规则沿用 0.3。
2. **输入**：
   - 本地数据库命中时（见上），就地读取。
   - 否则把 `InputUrl` 下载到输入缓存，下载的同时计算 SHA-256。缓存按 ItemId、长度和修改时间命名，有容量上限。
   - 302 跳转时不带凭据跟随。
3. **识别**：发送 SHA-256、ffprobe 结果和根，得到编码计划；收到 `Done` 就结束。
4. **编码**（按计划；参数已在附录 B 验证）：
   - **视频**：一次解码，多路输出。`split` 之后，每个档位各自去隔行、色调映射、缩放、设定帧率和编码，输出 `init.mp4` 和 `segment-%06d.m4s`。`-hls_time` 取计划的切片长度，关键帧间隔取计划值，闭合 GOP。
   - **音频**：一次运行输出全部音轨，AAC，声道和码率由计划给出。
   - **字幕**：文本字幕转 WebVTT，带 `X-TIMESTAMP-MAP`。
   - **预览图**：截一帧。
   - **自检**：编完后核对文件齐全、段号连续、段数和时长在误差内；算出峰值和平均码率，以及 `avc1` 编码字符串。
5. **上传**：每编完一个档位就上传（少占磁盘；失败时租约到期后删除队列清理半成品），用根所属连接的凭据上传到 `根/计划给出的目录`（S3 带 `Content-Type` 和 `Content-MD5`；本地目录经 SFTP，先写临时名再改名）。单个任务内并发 4 个文件，暂时性错误重试。
6. **完成**：上报每个档位的摘要、文件列表和各段时长，收到 202 后删除任务目录。失败时报告 `Failed`，同样清理目录，开启 `KeepFailedJobs` 时保留。

**并发**：`Concurrency` 个任务同时取输入、识别和编码。编完的任务等待 `UploadConcurrency` 个上传名额，这时下一个任务可以开始编码。

**`check`**：ffmpeg 和 ffprobe 的版本；用配置的编码器编两帧；工作目录可写；协议版本；每条连接可用；HLS 根用自己的凭据写删一个测试对象，并经 `Bases/Check` 通过。

**发布形式**：自包含包（win-x64、linux-x64、linux-arm64）和 systemd unit（无头宿主），脚本随之更新；README 重写。

## 七、播放与交付

### 7.1 源列表与原片策略

**由谁管理**：选定媒体库中有已发布 HLS 的条目，由 `MediaSourceManager` 直接组织它的源列表，不再使用装饰器。没有用户的调用（服务器内部的流、字幕、附件处理）仍然得到原始源，与插件相同。

**源列表**由服务器命名并排序：`自动`、`源文件 (1080p 60fps)`、各档位从高到低，只列已发布档位。
- 没有播放权限，或者不允许原片且 HLS 尚未发布时，返回 `NoCompatibleStream`。
- 「自动」在允许原片、原片能播放、且请求来自内网时，解析为原片（在 `MediaInfoHelper` 中处理）；否则解析为 HLS。
- 「自动」只在原片能直接播放、或用户有视频转码权限时解析为原片。没有指定源时，「自动」解析为原片的情况下原片排在最前（正在续播另一个版本时该版本保持最前），其余保持服务器的顺序；已发布条目的源不再按码率等重新排序，也不再探测原片。
- 已发布时，这台设备既不能直接播放、又不能转码的原片不列出（原片跟随用户的转码权限）。
- HLS 源的 id 沿用插件的版本 id（`GetNewItemId("https://distributed-hls.invalid/{条目}/{档位}/master.m3u8", Video)`），流的序号沿用原片的 ffprobe 序号；PlaybackInfo 为每个 HLS 源签发授权，`TranscodingUrl` 指向 `/HlsPlayback/{grant}/{名称}.m3u8`，字幕以外部文件交付（`r/subtitles/{序号}/{attempt}/subtitle.vtt`）。
- 播放器中带 √ 的源列表是 Web 第 7 阶段的工作。

**原片出现的条件**：
- 已发布 HLS 时：`AllowOriginal`、未归档、且（请求来自内网并开启 `AllowLocalOriginal`，或用户的 `EnableOriginalPlayback`）。
- 未发布 HLS 时：只看归档（归档时没有原片，也就没有可播放的源）。
- 使用 API Key 的请求不受限制。
- 备用版本：每个原片按它自己的条目是否已发布判断，备用版本也一样（已发布且不允许时不列出，流路由同样返回 403；未发布的备用版本照常列出和播放）。
- HLS 源的字幕以外部文件交付，但不标为外部字幕，默认字幕的选择与原片的内嵌字幕相同。
- 归档并入这条规则；`StreamingHelpers` 与 `MediaInfoHelper` 现有的归档检查保留作兜底。原计划中「与归档相互独立」作废。
- 不允许时，以下路由对原片返回 403：
  - `/Items/{id}/File`、`/Items/{id}/Download`；
  - 静态流和实时流路由：`VideosController`、`AudioController`、`UniversalAudioController`、`DynamicHlsController`，统一在 `StreamingHelpers` 中检查。

### 7.2 授权与播放列表

**授权**：匿名授权放在路径里（移植 `PlaybackGrant`），客户端不需要认证头。签名密钥为空时，第一次签发授权前生成并保存（第 4 阶段只校验格式：空，或至少 32 字节的 Base64）。`PUT Hls/Settings` 收到空密钥时保留现有密钥，保存设置不会让已签发的授权失效。授权无效或过期返回 `403`，条目不能播放、档位未发布返回 `404`。

**路由**（`HlsDeliveryController`）：
- `GET /HlsPlayback/{grant}/master.m3u8`、`{q}p.m3u8`，可加 `-a{序号}` 后缀：主播放列表，按请求时已发布的内容生成。
- `GET /HlsPlayback/{grant}/r/{kind}/{index}/index.m3u8`：媒体播放列表，**由服务器生成**，依据切片计划和保存的各段时长。
- `GET /HlsPlayback/{grant}/r/{kind}/{index}/{attempt}/{file}`：转发模式下的文件，按该档位的根读取。媒体播放列表的相对地址带上所发布的 attempt 目录，档位重新发布后，正在播放的客户端在宽限期内仍读到原来那一套文件。已知限制：档位改发布到另一个根时，旧 attempt 按新根查找，正在转发播放的客户端会得到 `404`，重新打开播放即可（换根很少见，不为它在每次文件请求时查删除队列）。
- `GET /HlsPlayback/Versions/{versionId}/master.m3u8`：已登录用户 302 到带授权的地址。核心自己的流路由遇到 HLS 源 id 时也 302 过去。

所有播放列表都带 `Cache-Control: no-store`，每次请求重新生成。

### 7.3 直连与转发

**直连**：媒体播放列表中的 `init.mp4`、切片和 `subtitle.vtt` 写成存储的外部地址（逐对象签名，按小时对齐），按**每个档位的根**生成。条件是：
1. 该档位的根是网络存储，且其访问权限设置了外部地址；
2. 档位已发布，即只指向校验通过的文件；
3. 授权有效。

同一个主播放列表中，各档位可以分别直连或转发。

**转发**：不满足以上条件时，写相对地址，由服务器发送本地文件，或从存储读取后转发。

**有效期**：直连地址的有效期取访问权限的「客户端地址有效期」，需要长于一次播放，包括暂停。Cloudflare Worker 的签名格式不变（`tools/network-storage-worker`）。

### 7.4 版本条目

移植 `VersionCatalog`（独立托管服务 `HlsVersionCatalog`）：每个已发布的档位和「自动」各建一个视频条目，作为原始条目的备用版本，供按 id 打开版本的客户端（如 Android TV）使用。
- 条目只带 `PrimaryVersionId` 和标记 `DistributedHls`（值 `{原条目}:{档位}`），id 沿用插件的公式；不设 `OwnerId`，原条目也不链接它，进度留在原条目，原条目的源列表也不含它。
- 每 5 秒同步发布索引 `Changed` 事件、原条目更新或删除涉及的条目，设置变化时同步全部；发布索引读完一次（`IsLoaded`）之前不同步，免得启动时误删。失败的条目 10 分钟后重试。
- 同步跳过版本条目；撤下档位、关闭或删除原条目时删除对应的 `HlsVersionItems` 和条目。
- 按版本条目 id 访问：源列表是原条目的源，它自己的排第一，只对能看到原条目的用户；PlaybackInfo 按原条目处理，默认请求它自己的源；视频流入口（`stream`、`master.m3u8`、`live.m3u8`）302 到授权地址；`HlsPlayback/Versions/{id}/master.m3u8` 302；其他流路由与 `File`、`Download` 返回 `404`。可见性只看原条目（版本条目是普通 `Video`，不按它自己的分级判断）；按版本条目上报的播放进度记在原条目上。删除版本条目前先清掉 `PrimaryVersionId`，否则删除时会按原条目的源清掉原条目提取的字幕与附件。
- 已知限制（与插件相同）：直接按版本条目 id 读取条目详情或用户数据（`/Items/{id}`、`UserData`）时，仍按版本条目自己判断可见性（未分级的普通 `Video` 归为「其他」）和读它自己的用户数据；客户端通常从原条目进入，不受影响。
- 不移植 `FindOriginalByPath`：插件要从版本条目的路径找回原条目，服务器在每个路由里直接按条目判断。

## 八、管理页与 API

**管理 API**（`HlsController`，需要管理员权限）：
- `GET`/`PUT Hls/Settings`：经命名配置 `hls` 保存，设置无效时 `400` 带原因。
- `GET Hls/Status`：启用、设置错误、有问题的根（`StoreProblem` 列出根及原因）、任务计数、最近一次校验。
- `GET Hls/Jobs?limit=`：默认 200，至多 1000。
- `POST Hls/Rebuild`：`202`。
- `POST Hls/Verify?itemId=`：`202`；已有校验在运行时 `409`。
- **删除** `POST Hls/Store/Test`。

**管理页**（jellyfin-web）：新页面「HLS 预编码」，放在侧栏「播放」下，分三个标签页：
- **状态**：任务计数、各根的状态、最近一次校验、删除队列；
- **设置**：
  - 启用；
  - 媒体库、全局档位，以及每个媒体库单独选档位（只能选全局开启的）；
  - 原片：全局允许、内网访问；
  - 高级设置；
- **任务**：最近的任务列表，以及「重建工作列表」「校验已发布成果」两个按钮。

用户编辑页增加「允许播放原片」（`EnableOriginalPlayback`）。播放器的源列表（带 √）也在第 7 阶段完成。

写法参照「网络存储」页面（react-query、MUI、globalize），文案只加 en-us 和 zh-cn。

## 九、插件数据迁移与停用

**迁移 routine**：`Jellyfin.Server/Migrations/Routines/*_MigrateDistributedHlsPlugin.cs`
- **数据**：存在 `data/distributed-hls/jobs.db`、且 HLS 表为空时导入。
  - 导入来源、媒体、已发布的档位及其清单、版本条目和删除队列。未完成的任务不导入，由任务中心重新计划。
  - 档位和删除记录的根取自插件的 `OutputPath`（转换规则见下）。
  - 完成后把文件改名为 `jobs.db.migrated`。
- **各段时长**：迁移来的档位没有保存这项数据。第一次生成播放列表时，从该档位的根读取它目录里的 `index.m3u8`，取出各段时长写入数据库。
- **设置**：从 `plugins/configurations/Jellyfin.Plugin.DistributedHls.xml` 转入 `hls.xml`（没有存储设置）。
- **根**：`OutputPath` 位于 S3Storage 插件的某个映射内时，转为该映射的 `s3://桶/前缀 + 余下路径`。
  - 该桶还没有访问权限时，用映射中的凭据建一条，不归档、不公开，外部地址需要管理员在网络存储页面设置。
  - 不在任何映射内时，作为本地目录。

**停用插件**：`PluginManager` 不再加载 DistributedHls（`1d46b74f-…`），状态标为 `NotSupported`，并在日志中说明功能已并入服务器。

**S3Storage 插件不停用**：它可能还在为媒体库提供 s3fs 挂载。迁移后 HLS 根不再是它映射的目录，它的 302 过滤器也就不会再碰 HLS。文档提示：
- 原来为 `OutputPath` 设置的映射和挂载可以删除；
- 直连改用核心网络存储的外部地址。用 Cloudflare Worker 时需要重新部署核心的 `tools/network-storage-worker`，因为授权格式不同于插件的 `/media/{token}/{key}`。

## 十、阶段与提交

**分支**：
- jellyfin：`feature/hls`，从 `master` 拉出（`feature/network-storage` 已合并）。每个功能从 `feature/hls` 拉出功能分支，完成并通过 review 后合并回 `feature/hls` 并推送。
- jellyfin-web：整条 revert 提交 `4bcb07c2e5`，删除远端 `distributed-hls`；本地 `redesign/decoupled-v2` 永不推送。第 7 阶段从 `master` 拉出 `feature/hls`。
- Worker：`suujonH/Jellyfin.HLS.Worker`，同样按功能分支开发，review 后提交推送。

**每个阶段**：构建 0 警告、测试通过、Codex review 直到没有问题（吹毛求疵的意见可以拒绝），然后提交并推送。提交作者一律 `suujonH <suujonh@gmail.com>`；提交、README 和代码中不出现任何 AI 相关署名或字样。

| 阶段 | 内容 | 仓库 |
| --- | --- | --- |
| 0 | **切点原型**：比较 hls muxer 与 segment muxer 在 TS 和 fMP4 下、`-copyts` 加 `-ss` 重新开始后的实际切点；验证预编码的均匀切片和一次解码多路输出。结论见附录，测试保留作回归，没有配置 ffmpeg 时跳过 | jellyfin |
| 1 | 切片计划、Planner、关键帧来源（含 MP4 提取器和有时限的按需提取）、播放列表写法、ffmpeg 切点与 seek 的规则（`HlsCuts`）。这一阶段只有独立的组件，控制器仍走旧链路 | jellyfin |
| 2 | 会话与编码运行：会话管理、`list.csv` 读取与切片就绪事件、重新开始的策略、节流和清理改用段序号、每次 run 重新解析源地址；控制器改用新链路：新的切片 URL、由计划写出的 `main.m3u8`、segment muxer 命令行（`EncodingHelper` 的关键帧参数改用时间列表）；迁移 `DeleteUnalignedKeyframeData`，启用 MP4 的按需提取（默认值与迁移 `AddMp4KeyframeExtraction`） | jellyfin |
| 3 | HLS 存储与网络存储扩展：存储抽象，S3 的 PUT 预签名、List、Delete，根目录标记，存储测试（PUT 与写入部分在 4d 删除） | jellyfin |
| 4 | 任务中心，分三次提交：4a 纯逻辑、实体与迁移、`HlsJobStore`（连同测试）；4b 设置、同步与排程、校验、删除、发布索引；4c Worker API、管理 API | jellyfin |
| 4d | **按根改造与协议裁减**：三张表的 `Base` 列与迁移 `AddHlsBase`，发布索引与校验中任务带根，存储按根打开与缓存；`HlsOptions` 删除 `Store`、`OriginalUsers`，新增 `AllowOriginal`（`LibraryQualities` 随 4e），`HlsSettingsSnapshot` 去掉存储与上传有效期；`UserPolicy.EnableOriginalPlayback`；根的校验、`Identify` 带根、`Bases/Check`；按根监视、校验、删除，`StoreProblem` 列出根；租约丢失的宽限改为一个租约长度；第四、六、八节标为「删除」的服务器代码与测试 | jellyfin |
| 4e | **档位三层过滤与预编码**：`LibraryQualities` 与校验、按内容取应有档位（替换三处）、`GET /HlsWorker/Libraries`、`POST /HlsWorker/Jobs/Eager` | jellyfin |
| 5 | 播放与交付：源列表与原片规则（7.1）、路由检查、授权与交付路由、服务器生成播放列表、逐档位的直连与转发、版本条目（含撤下时删除） | jellyfin |
| 6 | 6a `Jellyfin.TrueCache.Tool` 的新模式（jellyfin）；6b Worker 1.0：引擎库、连接库、SFTP、SQLite、添加媒体与预编码、无头 `run` 宿主、Avalonia GUI；与服务器端到端联调 | jellyfin、Jellyfin.HLS.Worker |
| 7 | 「HLS 预编码」管理页、用户的原片权限、播放器源列表 | jellyfin-web |
| 8 | 插件迁移与停用、新文档 `HLS.md`（中文，风格同 S3.md），补充 `S3.md` 和 `TRUE_CACHE.md`，删除本文件 | jellyfin |

## 关键文件

**jellyfin**：
- **新增**：
  - `src/Jellyfin.MediaEncoding.Hls/` 下：`Plans/`、`Sessions/`、`Playlist/HlsPlaylistWriter.cs`、`Published/`（移植 `DistributedHls.Core` 的纯逻辑：阶梯、档位、路径、探测解析、授权、主播放列表）；
  - `src/Jellyfin.MediaEncoding.Keyframes/Mp4/`；
  - `Jellyfin.Server.Implementations/Hls/`（JobStore、任务中心、根的校验、校验、删除、发布索引、存储、版本条目、源列表、原片策略）；
  - 接口与 DTO：`MediaBrowser.Controller/Hls/`、`MediaBrowser.Model/Hls/`；
  - 实体：`src/Jellyfin.Database/.../Entities/Hls*.cs`，加 Sqlite 迁移；
  - 控制器：`Jellyfin.Api/Controllers/{HlsWorkerController,HlsDeliveryController,HlsController}.cs`。
- **修改**：
  - 实时 HLS：`Jellyfin.Api/Controllers/DynamicHlsController.cs`；`Jellyfin.Api/Helpers/{DynamicHlsHelper,StreamingHelpers,MediaInfoHelper}.cs`；`src/Jellyfin.MediaEncoding.Hls/{Playlist/DynamicHlsPlaylistGenerator,Cache/CacheDecorator}.cs`；`MediaBrowser.MediaEncoding/Transcoding/TranscodeManager.cs`；`MediaBrowser.Controller/MediaEncoding/{EncodingHelper,TranscodingThrottler,TranscodingSegmentCleaner}.cs`。
  - 网络存储：`MediaBrowser.Controller/NetworkStorage/INetworkStorageManager.cs`；`Jellyfin.Server.Implementations/NetworkStorage/{NetworkStorageManager,S3StorageProtocol}.cs`；`MediaBrowser.Common/Net/S3/`。
  - 用户策略：`UserPolicy.EnableOriginalPlayback`（实体、DTO 与迁移按现有用户策略字段的写法）。
  - TrueCache 工具：`src/Jellyfin.TrueCache.Tool/`。
  - 播放接入：`Emby.Server.Implementations/Library/MediaSourceManager.cs`；`Jellyfin.Api/Controllers/{LibraryController,VideosController,AudioController,UniversalAudioController}.cs`。
  - 注册与其他：`Emby.Server.Implementations/ApplicationHost.cs`、`Jellyfin.Server/Startup.cs`（后台服务）、`Emby.Server.Implementations/Plugins/PluginManager.cs`、迁移 routine。

**Jellyfin.HLS.Worker**（整个仓库重写）：
- 引擎库：服务器客户端、连接库（S3、SFTP）、任务执行与心跳、输入缓存（边下载边算哈希）、识别、编码（ffmpeg 参数，一次解码多路输出）、上传、添加媒体、TrueCache 子进程、SQLite、自检；
- 宿主：无头 `run`、Avalonia GUI；
- `tests/`、`deploy/`、`scripts/`、README。

**jellyfin-web**：
- 新增 `src/apps/dashboard/features/hls/`、`routes/hls/`；
- 修改侧栏 `ServerDrawerSection.tsx`、`_asyncRoutes.ts`、用户编辑页、播放器源列表，以及 `strings/en-us.json` 和 `zh-cn.json`。

**复用的现有实现**：
- `S3StorageProtocol.GetExternalAddress` 和 `AlignToHour`：直连地址；
- `NetworkStorageManager.ResolveShortcutTarget`：服务器读取、run 的源地址、Worker 输入的 302；
- `S3UrlSigner.GetUrl(..., method, parameters)`：List、Delete；
- `TranscodeManager.StartFfMpeg` 和 `KillTranscodingJobs`；
- `CacheDecorator` 和 `KeyframeRepository`；
- `FileStreamResponseHelpers.GetStaticRemoteStreamResult`：从存储转发；
- 插件的 `DistributedHls.Core` 及其测试；
- Worker 0.3 中已验证的规则：心跳容错、输入缓存、`avcC` 编码字符串、HDR 色调映射滤镜，以及 S3 与 Local 上传。

## 验证

1. **构建与测试**：
   - jellyfin：按项目 `dotnet build -c Release`，要求 0 警告；运行受影响的测试项目（MediaEncoding.Hls、Keyframes、Server.Implementations、Api、Integration、Server）。
   - Worker：`dotnet build`、`dotnet test`，测试用回环 HTTP 服务器、假 ffmpeg 和本地 SFTP/S3 替身。
   - jellyfin-web：tsc、eslint、vitest。
2. **切点测试**（`tests/Jellyfin.MediaEncoding.Hls.Tests/Ffmpeg/`）：设置环境变量 `JELLYFIN_FFMPEG` 指向 ffmpeg（同目录需要 ffprobe，且有 libx264）后运行，否则跳过。覆盖附录中的每条结论。
3. **单元测试**：
   - 计划：关键帧计划、均匀计划、小数帧率、末段；
   - 实时会话：重新开始的策略和就绪等待，用假 runner；
   - 识别：去重、来源已变化时回 409、计划内容、根的校验与记录；
   - 档位：三层过滤、多来源并集、并集缩小时撤下、没有来源时取内容的媒体库、设置校验；
   - 预编码任务：Done、409、子集校验、计划内容、孤儿时间、之后与普通任务一致；
   - 按根：多根并存时只暂停不可信根上的记录；
   - S3：List 和 Delete 的解析；
   - 交付：逐档位直连与转发生成的播放列表、授权校验；原片规则；
   - JobStore 状态机；
   - 插件迁移：用样例 `jobs.db`。
4. **集成测试**：一个假 Worker 走完整个流程：领取 → 识别（带本地根）→ 编码计划 → 直接写入根 → 完成 → 校验 → 发布 → PlaybackInfo 出现档位 → 主播放列表和媒体播放列表 → 读取切片；另走一遍预编码任务。
5. **端到端**（MinIO 或 R2，加 Worker 1.0 和开发服务器）：
   - Worker 运行 `check`，再处理一个本地文件和一个 S3 上的 `.strm`；通过「添加媒体」上 S3 并预编码一部分档位，扫描后其余档位补齐。
   - 设置外部地址后，网页播放器的网络面板里切片来自存储的域名；去掉外部地址后改由服务器转发；两个根并存时各档位分别处理。
   - 实时 remux 和转码中来回 seek，切片不重复也不缺失。
   - 归档存储上的 `.strm` 能生成 HLS 并播放，原片仍然不能播放。

## 附录 A：第 0 阶段结论（实时 HLS）

测量用 ffmpeg n8.1。样本为 23.976 fps 的 H.264（含 B 帧）+ AAC，关键帧间隔不规则（0.4 秒到 6.3 秒），封装为 mkv、mp4、ts；另有视频比音频晚 0.3 秒开始的 mkv。

### A.1 为什么不用 hls muxer

- hls muxer 在「距本次 run 起点 k × hls_time 之后的第一个关键帧」切开，按 run 起点累计，重新开始后切点与计划对不上。
- 输入 `-ss` 遇到含 B 帧的流时，ffmpeg 会把目标提前 3/23 秒（dts 启发式），关键帧 seek 不精确；旧的 +0.5 秒补偿可能落到下一个关键帧。
- 输出端 `-ss` 加复制会按 dts 平移时间戳，不能用来对齐视频。

### A.2 GOP 文件与列表

- segment muxer 加 `-segment_time 0.001`：每个关键帧开一个文件。
- `-segment_list list.csv -segment_list_type csv`：每行 `文件名,起点,终点`，单位秒，在输出时间轴上，六位小数。
  - run 的第一行起点为 0：muxer 不记录 run 首个文件的起点。
  - 终点等于下一个文件的起点，误差不超过 1 毫秒。
  - 第 j 行在文件 j 关闭之前写出；文件 j+1 在文件 j 关闭之后才创建。
- **计划的第 k 段** = 同一次 run 中、首个文件以外、起点落在 [B_k − tol, B_{k+1} − tol) 的文件，按顺序拼接。tol 取半帧左右。
  - 文件起点晚于边界（关键帧数据与实际不符，或只能按 GOP 长度出关键帧的编码器从不在网格上的首帧数起）时，仍属于起点所在的段。各段首尾相接，各次 run 相同，只是段长与播放列表不符，与旧链路相当。
  - 范围内没有文件时，跨过该段的那个文件（下一个文件之前的一个；run 已结束时为最后一个）同时作为该段发送。
- **就绪**：某行的终点 ≥ B_{k+1} − tol，且下一个文件已存在或 ffmpeg 已正常退出，第 k 段即完成。
- 关键帧之间的距离不影响正确性：一段可以由多个 GOP 文件组成，转码时额外的关键帧也只是多一个文件。

### A.3 时间戳在各次 run 之间保持一致

共同参数：`-copyts -start_at_zero -avoid_negative_ts disabled -f segment -max_delay 5000000 -individual_header_trailer 0`。

- **TS**：`-segment_format mpegts -segment_format_options avoid_negative_ts=disabled`。文件中的 PTS = 时间轴 + 10 秒（mpegts 加 2 × max_delay），每次 run 相同。
- **fMP4**：`-segment_format mp4 -segment_header_filename init.mp4 -segment_format_options movflags=+frag_custom+empty_moov+frag_discont+skip_sidx+default_base_moof:use_editlist=0:avoid_negative_ts=disabled:output_ts_offset=10`；TS 源另加 `-bsf:a aac_adtstoasc`。
  - 不能有编辑列表：有编辑列表时，tfdt 会随本次 run 首包的合成偏移（B 帧延迟）平移；毫秒精度的源每次 run 取整不同，相差 1 毫秒。
  - `output_ts_offset=10` 只作用于内层 muxer，让开头的负 dts 变为正数；CSV 列表仍是原时间轴。
  - init 不含样本。mkv、mp4 源各次 run 的 init 字节相同；TS 源只有码率字段不同。混用不同 run 的切片、配第一次 run 的 init，可以正常解码。会话只发送第一次 run 的 init。
- **复制的音频**：ffmpeg 按采样数从本次 run 的首包重建时间戳，毫秒精度的源（mkv）各次 run 相差不到 1 毫秒。视频时间戳原样保留，各次 run 完全相同。
- **外挂图形字幕烧录**：字幕是另一个输入，`-start_at_zero` 会按它自己的起点平移，与视频错开。字幕输入加 `-isync 0`（ffmpeg 5.1 起），改按媒体输入的起点平移，与旧链路不加 `-start_at_zero` 时的相对位置相同（已用两个起点不同的输入验证）。更早的 ffmpeg 照旧不加 `-start_at_zero`，时间轴只在源从 0 开始时与计划一致。

### A.4 重新开始

- **remux**：
  - `-ss` 取第 n 段边界之前那个关键帧再减 1 秒；不大于 0 时不 seek。
  - TS 没有索引，seek 落在任意包上，复制从其后的第一个关键帧开始；mkv、mp4 落在 seek 点之前的关键帧。所以 B_n 总是 run 中的非首个文件，起点有记录。
  - run 的首个文件丢弃：起点未知，而且带着关键帧之前的音频。
  - 从边界起的文件与完整 run 的包和时间戳相同（复制的音频见 A.3）；把完整 run 的第 n−1 段和重开 run 的第 n 段接起来，解码无错误，包与完整 run 相同。
- **转码**：
  - 段长按帧取整：L' = ceil(L × fps) / fps。
  - 精确 seek：`-ss (B_n − 半帧)`；run 的首个文件就是第 n 段。
  - `-force_key_frames:0` 用绝对时间列表 `B_k − 半帧`（k ≥ max(n, 1)），加 `-sc_threshold:v:0 0`，`-g` 取三段的帧数。每段正好一个文件，起点是离边界最近的帧，各次 run 相同。
  - 不能用表达式 `expr:gte(t,n_forced*L'-半帧)`：t 从本次 run 的首帧算起，视频起点不在计划网格上时（例如晚 0.3 秒开始），重开后强制的帧与完整 run 不同。
  - 不能强制关键帧的编码器（QSV、NVENC、AMF、RKMPP、SVT-AV1）只设 GOP 为一段的帧数，从 run 的首帧数起。视频晚于音频开始时，从头开始的 run 的文件都晚这个偏移，按 A.2 仍归入各段；重开的 run 从边界开始，与之交界处重叠这个偏移。
  - 列表长度：一次转码 run 最多 600 段（6 秒一段约 1 小时），用输出 `-to (B_End − 半帧)` 在下一次 run 的边界之前正常结束；之后的请求开始新的 run。这样命令行不会超过 Windows 的 32K 上限。`-copyts` 下输出 `-to` 是时间轴上的绝对位置，从中途开始的 run 也在 B_End 之前停止（已用测试验证）。
- **纯音频**：
  - 每个音频包都是关键帧，按时间切：`-segment_time L`，在「本次 run 首包 + k × L」之后的第一个包切开。
  - 转码重开：`-ss B_n`（精确 seek）。复制重开：输入 `-ss (B_n − 2)`，输出 `-ss B_n -output_ts_offset B_n`；输出 seek 会平移时间戳，offset 把它抵消。
  - 首包与边界相差不超过一个包，所以切点与计划相差不超过两个包；run 交界处与完整 run 可能相差一两个包（AAC 48 kHz 约 20–40 毫秒）。

## 附录 B：第 0 阶段结论（预编码）

- hls muxer，从 0 开始一次 run：`-hls_time 6`，`-force_key_frames expr:gte(t,n_forced*2)`，`-flags +cgop`，`-sc_threshold 0`，fMP4 切片。
- 一次解码、`split` 多路输出时，各视频档位的 EXTINF 完全相同；末段以外每段与 6 秒相差不超过一帧。
- 音频档位末段以外每段与 6 秒相差不超过一个 AAC 帧；段数与视频相同，或多一个很短的末段。

## 进度

- 2026-10-21：计划获批。第 0 阶段完成：切点测试在 `tests/Jellyfin.MediaEncoding.Hls.Tests/Ffmpeg/`，结论见附录。构建 0 警告；配置 ffmpeg 时 47 个测试全部通过，未配置时 23 个跳过。`feature/hls` 推送到 origin，以便在其他电脑上继续。
- 2026-10-21：第 0 阶段提交的 Codex review 补跑完成，没有问题。
- 2026-10-21：第 1 阶段完成：`Plans/`（`SegmentPlan`、`SegmentPlanner`、`HlsCuts`、`KeyframeSource`）、`Playlist/HlsPlaylistWriter`、MP4 提取器（只注册，旧链路的 MP4 默认值与迁移随第 2 阶段）、ffprobe 提取器减去 start_time 并只读第一条非封面视频流、设置 `KeyframeExtractionTimeoutSeconds`、旧链路只在复制第一条视频流时用关键帧。核对结论写在第二节。配置 ffmpeg 时 MediaEncoding.Hls 109 个、Keyframes 38 个测试全部通过。
- 2026-10-22：第 2 阶段完成：`Sessions/`（`HlsSessionManager`、`HlsSession`、`HlsEncoderRun`、`list.csv` 读取器与 `HlsSegmentLocator`）、控制器改用新链路（新的切片 URL、由计划写出的 `main.m3u8`、segment muxer 命令行、拼接发送 GOP 文件的 `ConcatenatedFileResult`）、节流与清理改用 run 给出的位置和文件、复用任务时重新解析源地址、迁移 `DeleteUnalignedKeyframeData` 与 `AddMp4KeyframeExtraction`。旧的 `DynamicHlsPlaylistGenerator` 删除，`IsExtractionAllowedForFile` 移入 `KeyframeSource`。配置 ffmpeg 时 MediaEncoding.Hls 142 个测试全部通过（含真实 ffmpeg 的 run 测试），Api 169 个、Controller 218 个、MediaEncoding 194 个、Server 41 个、Integration 129 个通过。
- 2026-10-22：第 3 阶段完成：`IHlsStore` 与 `IHlsStoreFactory`，本地目录与网络存储两种实现（根目录标记、写入、列出、删除、地址、存储测试）；`INetworkStorageManager` 的客户端、服务器、上传地址与列出、删除；`IObjectStorageProtocol` 与 `S3StorageProtocol` 的预签名 PUT、ListObjectsV2 分页、DeleteObjects 及其回退。S3 的应答按根元素核对（`ListBucketResult`、`DeleteResult`），代理的登录页等其他文档不会被当作空列表。Server.Implementations 中 HLS 存储与网络存储的 137 个测试全部通过（另有 2 个本地化测试因系统语言为中文而失败，与本阶段无关）。
- 2026-10-22：第 4a 阶段完成：`MediaBrowser.Model/Hls/` 的 DTO；`Published/` 的纯逻辑（探测解析、阶梯、档位、目录布局、完成校验）；七个实体与迁移 `AddHlsTaskCenter`；`HlsJobStore`（来源、媒体、来源任务与媒体任务的领取、识别、提交、发布、失败与租约、档位、孤儿、删除队列）。与 5.3 的差别已写回：「持有租约」以 `LeaseExpires` 为准，媒体任务只在内容有启用媒体库中的来源时创建，来源被删除时媒体任务回到等待，完成时释放档位，提交时按计划校验，来源任务换了内容时释放旧内容的档位。MediaBrowser.Model 与 Server.Implementations 构建 0 警告；MediaEncoding.Hls 295 个测试通过（未配置 ffmpeg，44 个跳过），Server.Implementations 中 HLS 的 105 个测试（其中 `HlsJobStore` 41 个）全部通过。
- 2026-10-22：第 4b 阶段完成：命名配置 `hls`（`HlsOptions`，保存时校验）、`HlsSettings`、`HlsRootMonitor`、发布索引 `HlsPublishedIndex`、任务中心循环 `HlsCoordinator`（同步、排程、租约回收、全量排程、孤儿）、`HlsVerificationService`（校验、发布、校验已发布成果）、`HlsDeletionService`，三个服务注册为托管服务。与 5.2 的差别已写回：只取 `VideoFile`、`.strm` 的大小只取 TrueCache 条目、多媒体库任选其一、失败在下次全量排程时重试、根目录只在有工作时检查、宽限期暂用播放授权有效期、索引刷新失败自动补读、删除队列的已知限制；签名密钥的生成与版本条目的排除留到第 5 阶段（见 7.2、7.4）。另有一个提交修复 Debug 构建的分析器错误（master 的网络存储代码与前几个阶段的 HLS 代码），WSL 下 Debug 构建 0 错误。Release 构建 0 警告；Server.Implementations 中 HLS 的 163 个测试全部通过（另有 2 个本地化测试因系统语言为中文而失败），MediaEncoding.Hls 300 个（44 个跳过）、Integration 129 个通过。
- 2026-10-22：第 4c 阶段完成：`IHlsTaskCenter` 与 `HlsTaskCenter`（领取、输入、心跳、识别、上传地址、本地写入、完成、失败、上传测试、状态与任务列表），`HlsWorkerController`、`HlsController`，`HlsWorkerProtocolAttribute`。与第六、八节的差别已写回：Worker 只接受 API Key（用户会话 `403`），错误码 `400`/`409`/`503` 带原因，`InputUrl` 与本地上传地址为相对路径，本地存储的上传测试走服务器路由，测试对象经删除队列删除，存储测试可指定位置；与 5.2 的差别：根目录不可信时不签发也不接收上传，租约结束后目录等上传地址过期再删除。Release 构建 0 警告，WSL 下 Debug 构建 0 警告；Server.Implementations 中 HLS 的 182 个测试全部通过（另有 2 个本地化测试因系统语言为中文而失败），Api 173 个、MediaEncoding.Hls 300 个（44 个跳过）、Integration 132 个通过。
- 2026-10-22：设计变更写回计划：Worker 自持凭据并上报存储根、服务器去掉存储设置与上传通道、档位三层过滤、添加媒体时的预编码任务、原片规则并入归档与用户权限、Worker 仓库改为 `Jellyfin.HLS.Worker`。新增第 4d、4e 阶段。
- 2026-10-22：第 4d 阶段完成：三张表的 `Base` 列与迁移 `AddHlsBase`；存储只读（删去 PUT 预签名、本地写入、标记写入与存储测试），由 Worker 写标记；`HlsOptions` 删去 `Store`、`OriginalUsers`，新增 `AllowOriginal`；`UserPolicy.EnableOriginalPlayback`；`Identify` 带根，根的校验与 `Bases/Check` 共用一个实现（本地根与媒体库互不包含、不在数据目录下）；监视、校验、再校验、删除都按根进行，一个根不可信只停它自己的工作，`StoreProblems` 只列仍在使用的根；`Base` 为空或无效的删除记录直接丢弃，不删除任何东西；租约结束后的宽限为一个租约长度；上传通道的路由、方法、DTO 与测试全部删除。Release 构建 0 警告；Server.Implementations 1320 个测试通过（2 个本地化测试因系统语言为中文而失败），Api 173 个、MediaEncoding.Hls 300 个（44 个跳过）、Integration 132 个通过。
- 2026-10-22：第 4e 阶段完成：`HlsOptions.LibraryQualities`（`HlsLibraryQualities`）与校验；`HlsSettingsSnapshot.GetQualities`；内容的应有档位取其来源所在媒体库的并集，没有时取 `HlsMedia.LibraryId`，仍为空时不同步，替换了同步、识别两处（`HlsIdentifyRequest.GetQualities`、`SyncRenditionsAsync` 改为按媒体库取档位）；`GET /HlsWorker/Libraries`；`POST /HlsWorker/Jobs/Eager`（`HlsJobStore.StartEagerJobAsync`），预编码任务没有输入，`Input` 回 `404`，重复 `Identify` 返回原计划。与计划的差别已写回：`Libraries` 变化也触发全量同步，交集校验只针对选中的媒体库，预编码要求媒体库存在。Release 构建 0 警告；Server.Implementations 中 HLS 的 184 个测试、Api 173 个、Integration 132 个通过。
- 2026-10-22：第 5a 阶段完成：`HlsPlaybackGrant`、`HlsMasterPlaylistWriter`（移植，无标题的音轨、字幕改用英文名）与移植的测试；`IHlsPlaybackService`/`HlsPlaybackService`（签发授权时生成并保存密钥，播放只读发布索引，视频、音频的媒体播放列表经 `GetPublishedSegmentsAsync` 读取各段时长，按档位的根写外部地址或相对地址；字幕为整段一个文件）；`HlsDeliveryController`（`HlsPlayback/{grant}/…`，本地文件带 Range 发送，网络存储读取后转发）；`IHlsStore.GetClientAddressLifetime` 与网络存储的地址有效期，发布时被替换目录的宽限按旧根计算。Release 构建 0 警告；Server.Implementations 1362 个测试通过（2 个本地化测试因系统语言为中文而失败），Api 173 个、Controller 218 个、MediaEncoding.Hls 412 个（44 个跳过）、Integration 136 个通过。
- 2026-10-22：第 5b 阶段完成：`IHlsPlaybackService` 增加 `IsPublished`、`IsOriginalAllowed`、`PrefersOriginal`、`ArrangeMediaSources`、`GetQuality`、`PrepareForPlayback`（原片规则读当前请求：API Key、内网、用户权限）；`MediaSourceManager` 对有用户的调用整理源列表（「自动」、源文件、各档位从高到低；没有播放权限时为空），已发布条目不探测、不加动态源、不排序；`MediaInfoHelper` 为 HLS 源签发授权并设置字段，跳过设备协商与排序；`MediaInfoController` 中「自动」按 7.1 解析为原片或 HLS；`StreamingHelpers` 对 HLS 源 id 返回 `404`（第 5c 阶段改为 302），按实际播放的源（含转码任务复用的源）检查原片，不允许时 `403`；HLS 源的音轨、字幕保留原片的强制、听障、原声标记；条目 DTO 的 `MediaStreams` 在源列表不含原片时取原片的流；`/Items/{id}/File`、`/Items/{id}/Download` 不允许时 `403`；`HlsEncodingLadder.GetName` 给原片命名（如「源文件 (1080p 24fps)」），本地化键 `HlsSourceAuto`、`HlsSourceOriginal`。Release 构建 0 警告；Server.Implementations 1381 个测试通过（2 个本地化测试因系统语言为中文而失败），Api 173 个、MediaEncoding.Hls 421 个（44 个跳过）、Integration 136 个通过。
- 2026-10-22：第 5c 阶段完成：版本条目 `HlsVersionCatalog`（见 7.4），发布索引增加 `Changed`、`IsLoaded`、`GetPublishedItems`；`IHlsPlaybackService` 增加 `GetOriginalId`、`GetPlaylistUrl`；视频流入口对 HLS 源 id 和版本条目 302 到授权地址（`HlsPlaybackRedirect`），`HlsPlayback/Versions/{id}/master.m3u8` 302；`MediaSourceManager`、`MediaInfoController` 按原条目处理版本条目；任务中心不再把版本条目当作库文件。Release 构建 0 警告；Server.Implementations 1399 个测试通过（2 个本地化测试因系统语言为中文而失败），Api 173 个、Integration 136 个通过。
- 2026-10-22：第 6a 阶段完成：`Jellyfin.TrueCache.Tool` 增加 `strm` 命令（按本地文件生成将来 `.strm` 的条目，写到 `--output`，`source.size` 取本地文件大小，不写 `source.modified`），以及 `--server-url`/`--api-key` 从服务器 API 读取系统与编码设置。用 ffmpeg 生成的样片验证：条目放进媒体库目录后 `validate` 通过；导入的设置不带服务器本机的路径（缓存、元数据、转码临时目录、ffmpeg），`--server-config` 同样处理。Release 构建 0 警告。
- 2026-10-22：第 6b 阶段完成（`Jellyfin.HLS.Worker` 的 `main`，分四个功能分支）：6b1 引擎库与无头 `run`、`check`（服务器客户端、任务执行与心跳、输入缓存、识别、编码、上传、本地数据库）；6b2 添加媒体（S3 与直接放入媒体库、`.strm` 与 TrueCache 条目、随添加的预编码）；6b3 Avalonia 桌面程序；6b4 发布包（win-x64、linux-x64、linux-arm64，TrueCache 工具随包发布）与 systemd 部署脚本，README 重写。与第六节的差别已写回：`.strm` 以去掉扩展名的文件名命名，本地数据库只记添加过的媒体。
- 2026-10-22：第 7 阶段完成（jellyfin-web 的 `feature/hls`）：7a 播放器的版本菜单（切换源时保留音轨、字幕，SyncPlay 中不提供，多段视频切换后排入其余部分）；7b「HLS 预编码」管理页（状态、设置、任务三个标签页）；7c 用户编辑页的「允许播放原片」。文案只加 en-us 和 zh-cn。
- 2026-10-22：第 8 阶段完成：迁移 `MigrateDistributedHlsPlugin`（设置、原片用户、数据，根按 S3Storage 的映射转换）；迁移来的档位在第一次生成播放列表时从 `index.m3u8` 读取并记下各段时长；`PluginManager` 不再加载 DistributedHls。Release 构建 0 警告；Server 46 个、Api 173 个测试通过，Server.Implementations 除 2 个本地化测试（系统语言为中文）外全部通过。文档写入 `HLS.md`，补充 `S3.md`、`TRUE_CACHE.md`，本文件随之删除。
