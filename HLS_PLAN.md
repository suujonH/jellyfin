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
- Worker 自己保存存储凭据。
- 核心已经有网络存储（访问权限、外部地址、逐对象签名）和 TrueCache，两套机制重复。

**目标**：
- 一套 HLS 模型：实时转码（服务器本机）与预编码（Worker）共用切片计划和播放列表写法。
- 预编码成果可以放在本地目录或网络存储（S3）。存储设置了外部地址时，客户端直接从存储读取切片。
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
- **Worker 不保存任何存储凭据**：网络存储由服务器签发预签名 PUT；本地存储由 Worker 上传给服务器。
- **直连 = 存储设置了外部地址**，沿用「配置外部地址 = 公开」，不另设开关。
- **Worker 彻底重写，不保留历史包袱**：
  - 不兼容旧协议，去掉 `submit` 和存储映射。
  - 在 `Jellyfin.DistributedHls.Worker` 的 `main` 上直接重写，用一个提交替换现有的 0.3.0 提交，并强制推送到 `origin/main`。

## 一、总体结构

```text
PlaybackInfo → 源列表：原始 ／ 预编码档位（1080p…）／ 自动
  原始需要转码 → 实时 HLS：HlsSession → EncoderRun（本机 ffmpeg）→ 本地切片 → 服务器发送
  预编码档位   → 交付：授权 → 服务器生成播放列表 → 切片：存储直连 或 服务器转发

任务中心：媒体库同步 → 排程 → Worker 领取 → 识别（哈希 + 探测）→ 编码计划
          → 编码 → 申请上传地址并上传 → 完成 → 校验 → 发布 → 删除队列
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

**两种存储**（设置项 `Store`）：
- 本地目录 `LocalPath`；
- 网络存储前缀 `s3://桶/前缀`。
  - 这个桶必须有一条访问权限，且不能是归档：归档的存储客户端无法播放。
  - 只支持 s3，因为 http 不能写入。

**布局**沿用插件，迁移来的成果因此不需要移动：`{媒体}/{video|audio|subtitles|preview}/{序号}/{租约令牌}/`。

**根目录标记**：`.jellyfin-hls`，也接受旧名 `.distributed-hls`。
- 标记缺失、而已发布的成果也不在时，校验和删除暂停，不消耗重试次数。
- 网络存储同样适用，防止访问权限被改到别的桶后，系统误以为成果全部丢失、整库重新编码。

| 操作 | 本地目录 | 网络存储 |
| --- | --- | --- |
| Worker 上传 | `PUT` 到服务器（先写临时文件，再改名） | 服务器签发预签名 `PUT`，走访问权限的 Endpoint |
| 校验 | 逐个 stat，可选核对修改时间 | 按档位目录 `ListObjectsV2`，核对存在与大小 |
| 删除 | 删目录 | 列出前缀后批量 `DeleteObjects`，服务不支持时逐个 `DELETE` |
| 服务器读取（转发） | `PhysicalFile` | 按网络存储的规则读取：公开读外部地址，否则读 Endpoint |
| 客户端地址 | 无 | 外部地址，逐对象签名，按小时对齐 |

**网络存储的扩展**：
- `INetworkStorageManager` 新增以下方法，都以 `s3://桶/键` 为参数：
  - `GetClientAddress(target)`：外部地址；不公开或归档时返回 null；
  - `GetServerAddress(target, method)`；
  - `GetUploadAddress(target, lifetime)`；
  - `ListAsync(prefix)`；
  - `DeleteAsync(keys)`。
- 实现在 `S3StorageProtocol` 中，复用 `S3UrlSigner.GetUrl(..., method, parameters)`，再加上 ListObjectsV2 的 XML 解析（分页）。

## 五、任务中心（并入核心）

### 5.1 数据（`jellyfin.db`，EF Core 实体加迁移 `AddHlsTaskCenter`）

| 表 | 主要字段 |
| --- | --- |
| `HlsSources` | ItemId（主键）、LibraryId、Path（文件路径或 `.strm` 目标）、Length、ModifiedTicks、Sha256 |
| `HlsMedia` | Sha256（主键）、MediaKey（哈希前 32 位）、LibraryId、Probe（JSON）、OrphanedAt、CreatedAt |
| `HlsRenditions` | (Sha256, Key)、State（Missing/Published/Failed）、JobId、PublishedJobId、RelativeDirectory、Summary、Segments（各段时长）、Profile、PublishedAt |
| `HlsJobs` | Id、WorkKey（唯一：`source:{ItemId}` 或 `media:{Sha256}`）、ItemId、Sha256、Plan（JSON）、Status、Worker、LeaseToken、LeaseUntil、Attempt、Error、时间戳 |
| `HlsJobOutputs` | (JobId, Key)、RelativeDirectory、Summary、Files、Segments |
| `HlsDeletions` | RelativeDirectory（主键）、NotBefore、Attempts、Reason、LastError |
| `HlsVersionItems` | (ItemId, Quality)、VersionId、Signature |

**写入**：所有写操作都经过 `HlsJobStore`，在进程内的锁下以一个事务执行。任务中心是这些表唯一的写入者，所以替代了插件的 IMMEDIATE 事务。

**状态机**：以插件的为基础，租约、心跳、重试、取消、校验、发布和删除队列的规则不变；探测和编码合并为一种任务（见 5.2）。插件的 `JobStoreTests` 移植过来，并按新流程改写。

**设置**：放在命名配置 `hls`（`HlsOptions`），包括：启用、媒体库、档位、存储、原始资源策略、租约、重试、孤儿保留、播放授权有效期和签名密钥、仅本地存储使用的「校验修改时间」。

### 5.2 流程

1. **同步**：后台循环，每 5 秒一次，并跟随媒体库事件。
   - 选定媒体库中的视频：本地文件，以及目标能解析的 `.strm`（s3、http(s)；归档存储也包括在内，预编码正好让归档原件也能播放）。
   - 记录路径或目标、大小、修改时间。`.strm` 的大小用 TrueCache 的 `MediaSize`，修改时间用 `.strm` 文件的。
2. **排程**：
   - 来源还没有哈希（新文件或指纹变化）：建任务 `source:{ItemId}`。
   - 哈希已知但有缺失的档位（新增档位、编码配置变化、校验失败）：建任务 `media:{Sha256}`，输入取该内容的任意一个来源，优先原条目。
3. **领取与识别**：Worker 领取任务，取得输入，算 SHA-256，运行 ffprobe，然后调用 `Identify`。任务中心：
   - 记下来源的哈希，按哈希保存探测结果，相同内容共享成果；
   - 由探测结果和档位配置得出应有的档位（沿用插件的阶梯规则）；
   - 缺失的档位没有其他任务负责时，本任务接手，返回编码计划，包括：
     - 每个档位的参数；
     - 切片计划：切片长度默认 6 秒，关键帧间隔 2 秒，以及预期段数，都计入编码配置版本；
     - 允许上传的文件名：`init.mp4`、`segment-000000.m4s` 起的切片（段数允许比计划多 1）、`subtitle.vtt`、`preview.jpg`；
   - 否则返回 `Done`，任务完成，也就是在这里去重；
   - `media:` 任务的输入哈希与预期不同时，说明来源已变化：回 409，并重新同步这个来源。
4. **编码与上传**：
   - Worker 每编完一个档位就申请上传地址，直接上传到存储。
   - 目录名含租约令牌，Worker 无法覆盖已发布的成果。
   - 预签名地址有效期 1 小时，过期（403）后重新申请。
5. **完成**（202）：服务器核对：
   - 档位和文件是否在计划之内；
   - 段号是否从 0 连续；
   - 段数与计划相差是否不超过 1；
   - 末段以外每段的时长，与计划相差是否不超过一帧（视频）或一个 AAC 帧（音频）。附录 B 的测量支持这条规则。

   上报的各段时长会保存下来，用来生成播放列表。
6. **校验**：本地目录逐个 stat；网络存储按档位目录 List，核对大小。根目录不可信时暂停。
7. **发布**：在一个事务里把档位切换到新目录。被替换的目录在宽限期后删除，宽限期取播放授权有效期与存储外部地址有效期中较长的一个。
8. **删除队列**：失败按 1、2、4 分钟……退避重试，最长 6 小时，不阻塞派发。
9. **租约**：心跳续期。过期算一次失败，计划中的目录在一个租约长度之后删除。
10. **孤儿**：没有任何来源引用的内容，超过保留时间后删除它的成果。

### 5.3 与插件相比的变化

| 方面 | 插件 | 核心 |
| --- | --- | --- |
| 数据 | 独立的 `jobs.db` | `jellyfin.db` |
| 任务 | 探测、编码两种任务，加定向领取 | 一种任务：领取 → 识别 → 计划 → 编码 → 上传；识别时去重 |
| 成果位置 | 服务器路径，放 S3 时要挂 s3fs | HLS 存储，不需要挂载 |
| 上传 | Worker 自带映射和凭据 | 服务器签发地址 |
| 来源 | 只有本地文件 | 本地文件和 `.strm` |
| 播放列表 | Worker 生成，服务器原样发送 | 服务器按切片计划和上报的时长生成 |
| 交付到存储 | S3Storage 插件 302，Worker 改写播放列表 | 媒体播放列表中直接写外部地址 |
| 播放接入 | 装饰器加全局过滤器 | 直接改 `MediaSourceManager`、`MediaInfoHelper` 和流路由 |

## 六、Worker 1.0（重写）

`Jellyfin.DistributedHls.Worker` 按核心任务中心重新设计。

**不再保留的旧功能**：
- 协议 2；
- `submit`（库外文件）；
- 探测任务和定向领取；
- 存储映射（S3/SCP/Local）以及 AWSSDK.S3 依赖。

**配置**（`worker.json`）：
- 连接：`Server`、`ApiKey`（环境变量 `JELLYFIN_HLS_API_KEY` 优先）、`Name`；
- ffmpeg：`Ffmpeg`、`Ffprobe`、`Encoder`（libx264、h264_nvenc、h264_qsv、h264_amf、h264_vaapi）、`HardwareDevice`、`Threads`；
- 运行：`WorkDirectory`、`Concurrency`、`UploadConcurrency`、`PollSeconds`、`ControlTimeoutSeconds`、`InputCacheGiB`、`KeepFailedJobs`；
- `Sources`（可选，服务器路径 → 本机路径）。

没有任何存储凭据。

**命令**：`init`、`check`、`run [--once]`、`--help`、`--version`。

**协议**：所有请求带 API Key（不接受用户会话）和请求头 `X-Jellyfin-Hls-Protocol: 1`。

| 方法与路径 | 请求 | 应答 |
| --- | --- | --- |
| `GET /HlsWorker/Protocol` | — | `{"Version":1}` |
| `POST /HlsWorker/Poll` | `{Worker}` | `200 {Lease, Source, InputUrl}`；没有任务时 `204` |
| `GET /HlsWorker/Jobs/{id}/Input?leaseToken=` | — | 本地文件返回文件流，支持 Range；`.strm` 返回 302，指向服务器当场签发的存储地址 |
| `POST /HlsWorker/Jobs/{id}/Heartbeat` | `{LeaseToken}` | `204`；租约失效时 `409` |
| `POST /HlsWorker/Jobs/{id}/Identify` | `{LeaseToken, Sha256, ProbeJson}` | `200 {Plan}` 或 `200 {Done:true}`；来源已变化或租约失效时 `409` |
| `POST /HlsWorker/Jobs/{id}/Uploads` | `{LeaseToken, Key, Files[{Path, Length}]}` | `{Files[{Path, Method, Url, Headers}]}` |
| `PUT /HlsWorker/Jobs/{id}/Files/{key}/{file}?leaseToken=` | 文件内容 | `204`（仅本地存储） |
| `POST /HlsWorker/Jobs/{id}/Complete` | `{LeaseToken, Renditions[{Key, Summary, Files[{Path, Length}], SegmentDurations}]}` | `202` 或 `409` |
| `POST /HlsWorker/Jobs/{id}/Failed` | `{LeaseToken, Error}` | `204` 或 `409` |
| `POST /HlsWorker/UploadCheck` | `{Worker}` | 一个测试对象的上传地址，供 `check` 使用；服务器随后删除该对象 |

**一个任务的流程**：
1. **领取**：得到租约和来源（ItemId、路径或目标、长度、修改时间）。
   - 每隔租约的三分之一发一次心跳。
   - `409` 立即停止；心跳连续失败、租约将尽时也停止，规则沿用 0.3。
2. **输入**：
   - `Sources` 映射命中且长度一致时，就地读取。
   - 否则把 `InputUrl` 下载到输入缓存，下载的同时计算 SHA-256。缓存按 ItemId、长度和修改时间命名，有容量上限。
   - 302 跳转时不带凭据跟随。
3. **识别**：发送 SHA-256 和 ffprobe 结果，得到编码计划；收到 `Done` 就结束。
4. **编码**（按计划；参数已在附录 B 验证）：
   - **视频**：一次解码，多路输出。`split` 之后，每个档位各自去隔行、色调映射、缩放、设定帧率和编码，输出 `init.mp4` 和 `segment-%06d.m4s`。`-hls_time` 取计划的切片长度，关键帧间隔取计划值，闭合 GOP。
   - **音频**：一次运行输出全部音轨，AAC，声道和码率由计划给出。
   - **字幕**：文本字幕转 WebVTT，带 `X-TIMESTAMP-MAP`。
   - **预览图**：截一帧。
   - **自检**：编完后核对文件齐全、段号连续、段数和时长在误差内；算出峰值和平均码率，以及 `avc1` 编码字符串。
5. **上传**：
   - 每个档位申请地址，每批至多 500 个文件。
   - 按地址 PUT，带 `Content-Type` 和 `Content-MD5`。单个任务内并发 4 个文件。
   - 暂时性错误重试；地址过期（403）时重新申请。
6. **完成**：上报每个档位的摘要、文件列表和各段时长，收到 202 后删除任务目录。失败时报告 `Failed`，同样清理目录，开启 `KeepFailedJobs` 时保留。

**并发**：`Concurrency` 个任务同时取输入、识别和编码。编完的任务等待 `UploadConcurrency` 个上传名额，这时下一个任务可以开始编码。

**`check`**：ffmpeg 和 ffprobe 的版本；用配置的编码器编两帧；工作目录可写；协议版本；通过 `UploadCheck` 实际上传一个测试文件。

**发布形式**：自包含包（win-x64、linux-x64、linux-arm64）和 systemd unit 保留，脚本随之更新；README 重写。

**仓库处理**：
1. 在 `main` 上直接重写。
2. 完成并验证后，先在本地给旧提交打标签 `archive/0.3.0`，不推送这个标签。
3. 用 `git commit --amend` 把唯一的提交替换为 `Distributed HLS Worker 1.0.0`，作者为 suujonH。
4. `git push --force origin main`。

## 七、播放与交付

### 7.1 源列表与原始资源策略

**由谁管理**：选定媒体库中有已发布 HLS 的条目，由 `MediaSourceManager` 直接组织它的源列表，不再使用装饰器。没有用户的调用（服务器内部的流、字幕、附件处理）仍然得到原始源，与插件相同。

**源列表顺序**：原始（允许时）→ 固定档位（从高到低）→ 自动。规则沿用插件：
- 没有播放权限，或者不允许使用原始资源且 HLS 尚未发布时，返回 `NoCompatibleStream`。
- 「自动」在请求来自内网、允许使用原始资源、且原始资源能播放时，解析为原始资源（在 `MediaInfoHelper` 中处理）。

**原始资源策略**：
- 允许使用原始资源的情况：内网请求（`AllowLocalOriginal`），或用户在 `OriginalUsers` 中。
- 不允许时，以下路由对原始资源返回 403：
  - `/Items/{id}/File`、`/Items/{id}/Download`；
  - 静态流和实时流路由：`VideosController`、`AudioController`、`UniversalAudioController`、`DynamicHlsController`，统一在 `StreamingHelpers` 中检查。
- 使用 API Key 的请求不受限制。
- 这项策略与网络存储的「归档」相互独立。

### 7.2 授权与播放列表

**授权**：匿名授权放在路径里（移植 `PlaybackGrant`），客户端不需要认证头。

**路由**（`HlsDeliveryController`）：
- `GET /HlsPlayback/{grant}/master.m3u8`、`{q}p.m3u8`，可加 `-a{序号}` 后缀：主播放列表，按请求时已发布的内容生成。
- `GET /HlsPlayback/{grant}/r/{kind}/{index}/index.m3u8`：媒体播放列表，**由服务器生成**，依据切片计划和保存的各段时长。
- `GET /HlsPlayback/{grant}/r/{kind}/{index}/{file}`：转发模式下的文件。
- `GET /HlsPlayback/Versions/{versionId}/master.m3u8`：已登录用户 302 到带授权的地址。核心自己的流路由遇到 HLS 源 id 时也 302 过去。

所有播放列表都带 `Cache-Control: no-store`，每次请求重新生成。

### 7.3 直连与转发

**直连**：媒体播放列表中的 `init.mp4`、切片和 `subtitle.vtt` 写成存储的外部地址（逐对象签名，按小时对齐）。条件是：
1. HLS 存储是网络存储，且它的访问权限设置了外部地址；
2. 档位已发布，即只指向校验通过的文件；
3. 授权有效。

**转发**：不满足以上条件时，写相对地址，由服务器发送本地文件，或从存储读取后转发。

**有效期**：直连地址的有效期取访问权限的「客户端地址有效期」，需要长于一次播放，包括暂停。Cloudflare Worker 的签名格式不变（`tools/network-storage-worker`）。

### 7.4 版本条目

移植 `VersionCatalog`：每个已发布的档位和「自动」各建一个视频条目，作为原始条目的备用版本，供按 id 打开版本的客户端（如 Android TV）使用。

## 八、管理页与 API

**管理 API**（`HlsController`，需要管理员权限）：
- `GET`/`PUT Hls/Settings`
- `GET Hls/Status`
- `GET Hls/Jobs?limit=`
- `POST Hls/Rebuild`
- `POST Hls/Verify?itemId=`
- `POST Hls/Store/Test`：写入、读取、删除一个测试对象，并检查根目录标记。

**管理页**（jellyfin-web）：新页面「HLS 预编码」，放在侧栏「播放」下，分三个标签页：
- **状态**：任务计数、存储状态、最近一次校验、删除队列；
- **设置**：
  - 启用；
  - 媒体库、档位；
  - 存储：本地目录，或选择网络存储访问权限并填写前缀；
  - 原始资源：内网访问、指定用户；
  - 高级设置；
- **任务**：最近的任务列表，以及「重建工作列表」「校验已发布成果」两个按钮。

写法参照「网络存储」页面（react-query、MUI、globalize），文案只加 en-us 和 zh-cn。

## 九、插件数据迁移与停用

**迁移 routine**：`Jellyfin.Server/Migrations/Routines/*_MigrateDistributedHlsPlugin.cs`
- **数据**：存在 `data/distributed-hls/jobs.db`、且 HLS 表为空时导入。
  - 导入来源、媒体、已发布的档位及其清单、版本条目和删除队列。未完成的任务不导入，由任务中心重新计划。
  - 完成后把文件改名为 `jobs.db.migrated`。
- **各段时长**：迁移来的档位没有保存这项数据。第一次生成播放列表时，从存储读取它目录里的 `index.m3u8`，取出各段时长写入数据库。
- **设置**：从 `plugins/configurations/Jellyfin.Plugin.DistributedHls.xml` 转入 `hls.xml`。
- **存储**：`OutputPath` 位于 S3Storage 插件的某个映射内时，转为该映射的 `s3://桶/前缀 + 余下路径`。
  - 该桶还没有访问权限时，用映射中的凭据建一条，不归档、不公开，外部地址需要管理员在网络存储页面设置。
  - 不在任何映射内时，作为本地目录。

**停用插件**：`PluginManager` 不再加载 DistributedHls（`1d46b74f-…`），状态标为 `NotSupported`，并在日志中说明功能已并入服务器。

**S3Storage 插件不停用**：它可能还在为媒体库提供 s3fs 挂载。迁移后 HLS 存储不再是它映射的目录，它的 302 过滤器也就不会再碰 HLS。文档提示：
- 原来为 `OutputPath` 设置的映射和挂载可以删除；
- 直连改用核心网络存储的外部地址。用 Cloudflare Worker 时需要重新部署核心的 `tools/network-storage-worker`，因为授权格式不同于插件的 `/media/{token}/{key}`。

## 十、阶段与提交

**分支**：
- jellyfin：`feature/hls`，从 `master` 拉出（`feature/network-storage` 已合并）。
- jellyfin-web：第 7 阶段从 `master` 拉出 `feature/hls`。
- Worker：按第六节，直接在 `main` 上工作，替换提交后强制推送。

**每个阶段**：构建 0 警告、测试通过、Codex review 直到没有问题，然后提交。提交作者 suujonH，与现有提交相同，不加署名行。

| 阶段 | 内容 | 仓库 |
| --- | --- | --- |
| 0 | **切点原型**：比较 hls muxer 与 segment muxer 在 TS 和 fMP4 下、`-copyts` 加 `-ss` 重新开始后的实际切点；验证预编码的均匀切片和一次解码多路输出。结论见附录，测试保留作回归，没有配置 ffmpeg 时跳过 | jellyfin |
| 1 | 切片计划、Planner、关键帧来源（含 MP4 提取器和有时限的按需提取）、播放列表写法、ffmpeg 切点与 seek 的规则（`HlsCuts`）。这一阶段只有独立的组件，控制器仍走旧链路 | jellyfin |
| 2 | 会话与编码运行：会话管理、`list.csv` 读取与切片就绪事件、重新开始的策略、节流和清理改用段序号、每次 run 重新解析源地址；控制器改用新链路：新的切片 URL、由计划写出的 `main.m3u8`、segment muxer 命令行（`EncodingHelper` 的关键帧参数改用时间列表）；迁移 `DeleteUnalignedKeyframeData`，启用 MP4 的按需提取（默认值与迁移 `AddMp4KeyframeExtraction`） | jellyfin |
| 3 | HLS 存储与网络存储扩展：存储抽象，S3 的 PUT 预签名、List、Delete，根目录标记，存储测试 | jellyfin |
| 4 | 任务中心：实体与迁移、`HlsJobStore`（连同测试）、同步与排程、识别、校验、删除、发布索引、Worker API、管理 API | jellyfin |
| 5 | 播放与交付：源列表、原始资源策略与路由检查、授权与交付路由、服务器生成播放列表、直连与转发、版本条目 | jellyfin |
| 6 | Worker 1.0 重写；与第 4、5 阶段的服务器做端到端联调后，替换提交并强制推送 | Jellyfin.DistributedHls.Worker |
| 7 | 「HLS 预编码」管理页 | jellyfin-web |
| 8 | 插件迁移与停用、新文档 `HLS.md`（中文，风格同 S3.md），补充 `S3.md` 和 `TRUE_CACHE.md`，删除本文件 | jellyfin |

## 关键文件

**jellyfin**：
- **新增**：
  - `src/Jellyfin.MediaEncoding.Hls/` 下：`Plans/`、`Sessions/`、`Playlist/HlsPlaylistWriter.cs`、`Published/`（移植 `DistributedHls.Core` 的纯逻辑：阶梯、档位、路径、探测解析、授权、主播放列表）；
  - `src/Jellyfin.MediaEncoding.Keyframes/Mp4/`；
  - `Jellyfin.Server.Implementations/Hls/`（JobStore、任务中心、校验、删除、发布索引、存储、版本条目、源列表、原始资源策略）；
  - 接口与 DTO：`MediaBrowser.Controller/Hls/`、`MediaBrowser.Model/Hls/`；
  - 实体：`src/Jellyfin.Database/.../Entities/Hls*.cs`，加 Sqlite 迁移；
  - 控制器：`Jellyfin.Api/Controllers/{HlsWorkerController,HlsDeliveryController,HlsController}.cs`。
- **修改**：
  - 实时 HLS：`Jellyfin.Api/Controllers/DynamicHlsController.cs`；`Jellyfin.Api/Helpers/{DynamicHlsHelper,StreamingHelpers,MediaInfoHelper}.cs`；`src/Jellyfin.MediaEncoding.Hls/{Playlist/DynamicHlsPlaylistGenerator,Cache/CacheDecorator}.cs`；`MediaBrowser.MediaEncoding/Transcoding/TranscodeManager.cs`；`MediaBrowser.Controller/MediaEncoding/{EncodingHelper,TranscodingThrottler,TranscodingSegmentCleaner}.cs`。
  - 网络存储：`MediaBrowser.Controller/NetworkStorage/INetworkStorageManager.cs`；`Jellyfin.Server.Implementations/NetworkStorage/{NetworkStorageManager,S3StorageProtocol}.cs`；`MediaBrowser.Common/Net/S3/`。
  - 播放接入：`Emby.Server.Implementations/Library/MediaSourceManager.cs`；`Jellyfin.Api/Controllers/{LibraryController,VideosController,AudioController,UniversalAudioController}.cs`。
  - 注册与其他：`Emby.Server.Implementations/ApplicationHost.cs`、`Jellyfin.Server/Startup.cs`（后台服务）、`Emby.Server.Implementations/Plugins/PluginManager.cs`、迁移 routine。

**Jellyfin.DistributedHls.Worker**（整个仓库重写）：
- `src/DistributedHls.Worker/`：命令行与配置、服务器客户端、任务执行与心跳、输入缓存（边下载边算哈希）、识别、编码（ffmpeg 参数，一次解码多路输出）、上传、自检；
- `tests/`、`deploy/`、`scripts/`、README。

**jellyfin-web**：
- 新增 `src/apps/dashboard/features/hls/`、`routes/hls/`；
- 修改侧栏 `ServerDrawerSection.tsx`、`_asyncRoutes.ts`，以及 `strings/en-us.json` 和 `zh-cn.json`。

**复用的现有实现**：
- `S3StorageProtocol.GetExternalAddress` 和 `AlignToHour`：直连地址；
- `NetworkStorageManager.ResolveShortcutTarget`：服务器读取、run 的源地址、Worker 输入的 302；
- `S3UrlSigner.GetUrl(..., method, parameters)`：PUT、List、Delete；
- `TranscodeManager.StartFfMpeg` 和 `KillTranscodingJobs`；
- `CacheDecorator` 和 `KeyframeRepository`；
- `FileStreamResponseHelpers.GetStaticRemoteStreamResult`：从存储转发；
- 插件的 `DistributedHls.Core` 及其测试；
- Worker 0.3 中已验证的规则：心跳容错、输入缓存、`avcC` 编码字符串、HDR 色调映射滤镜。

## 验证

1. **构建与测试**：
   - jellyfin：按项目 `dotnet build -c Release`，要求 0 警告；运行受影响的测试项目（MediaEncoding.Hls、Keyframes、Server.Implementations、Api、Integration、Server）。
   - Worker：`dotnet build`、`dotnet test`，测试用回环 HTTP 服务器和假 ffmpeg。
   - jellyfin-web：tsc、eslint、vitest。
2. **切点测试**（`tests/Jellyfin.MediaEncoding.Hls.Tests/Ffmpeg/`）：设置环境变量 `JELLYFIN_FFMPEG` 指向 ffmpeg（同目录需要 ffprobe，且有 libx264）后运行，否则跳过。覆盖附录中的每条结论。
3. **单元测试**：
   - 计划：关键帧计划、均匀计划、小数帧率、末段；
   - 实时会话：重新开始的策略和就绪等待，用假 runner；
   - 识别：去重、来源已变化时回 409、计划内容；
   - 上传授权：只签计划内的文件，需要有效租约；
   - S3：List 和 Delete 的解析；
   - 交付：直连与转发两种模式下生成的播放列表、授权校验；
   - JobStore 状态机；
   - 插件迁移：用样例 `jobs.db`。
4. **集成测试**：一个假 Worker 走完整个流程：领取 → 识别 → 编码计划 → 上传到本地存储 → 完成 → 校验 → 发布 → PlaybackInfo 出现档位 → 主播放列表和媒体播放列表 → 读取切片。
5. **端到端**（MinIO 或 R2，加 Worker 1.0 和开发服务器）：
   - Worker 运行 `check`，再处理一个本地文件和一个 S3 上的 `.strm`。
   - 设置外部地址后，网页播放器的网络面板里切片来自存储的域名；去掉外部地址后改由服务器转发。
   - 实时 remux 和转码中来回 seek，切片不重复也不缺失。
   - 归档存储上的 `.strm` 能生成 HLS 并播放，原件仍然不能播放。

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
- 下一步：第 3 阶段（HLS 存储与网络存储扩展）。
