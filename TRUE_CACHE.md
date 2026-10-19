# `__true_cache__`：声明式媒体信息

媒体放在 S3（s3fs 挂载）、网盘或 NAS 等慢速存储上时，扫描、刷新、计划任务和字幕、附件提取都会反复读取媒体文件。`__true_cache__` 让用户事先声明服务器本来要从文件中读出的全部信息。有声明的资源，服务器只在**直接播放（静态发送、下载）**和**转码、Remux**时读取文件，其余一律读取声明。

这项功能对所有媒体库和存储方式生效，只看资源旁边有没有 `__true_cache__`，不需要任何配置。

## 目录布局

`__true_cache__` 与它描述的资源放在同一层，每个资源一个目录，目录名就是资源的文件名（光盘资源用文件夹名）：

```text
Movies/Film/
├── Film.mkv
└── __true_cache__/
    └── Film.mkv/
        ├── meta.toml                    声明与清单
        ├── probe.json                   ffprobe 原始输出
        ├── primary.jpg                  截图（Screen Grabber）
        ├── images/{primary|backdrop|logo}.{jpg|png|gif|bmp|webp}   内嵌图片
        ├── chapters/{StartPositionTicks}.jpg                       章节图
        ├── trickplay/{宽度} - {列}x{行}/{n}.jpg                     trickplay 拼图
        ├── subtitles/{流序号}.{srt|ass|ssa|sup|mks}                 已提取的内嵌字幕
        ├── attachments/{文件名}                                     已提取的附件（字体）
        ├── keyframes.json               关键帧
        ├── lyrics.lrc                   内嵌歌词（音频）
        ├── bdinfo.json                  蓝光结构（蓝光文件夹）
        └── book/                        ComicInfo.xml、comicbookinfo.json、content.opf、cover.*（书籍）
```

专辑整体的响度放在专辑文件夹的 `__true_cache__/__album__/meta.toml`。

删除或替换一个资源时，连同它的缓存目录一起处理即可。`__true_cache__` 目录本身不会出现在媒体库中。

## 完整性与拒绝

只有一个资源的**全部可能信息**都已声明，声明才有效。缺少任何一项、格式错误、版本未知，或 `source.size` 与实际文件不符时，该资源**不入库**；已经入库的条目会在下次扫描时移除。拒绝原因写入日志，也可以通过 `GET /TrueCache/Rejected` 查看。被拒绝的资源同样不会被读取。

| 类型 | 必需项 |
| --- | --- |
| 视频文件 | `probe.json`；有视频流时要截图、每个有图的章节一张章节图、每个配置宽度一组 trickplay（时长短于 trickplay 间隔时不需要）、`keyframes.json`；每张内嵌图片；每条可提取的内嵌字幕；每个附件（mjpeg 除外）；ts/m2ts 容器还要 `video.timestamp` |
| `.strm` | `probe.json`、内嵌字幕、附件（截图、章节图、trickplay、关键帧服务器本来就不生成） |
| DVD 文件夹 | `probe.json`（第一个 vob）、`video.runtime_ticks`（全部 vob 的总时长）、章节图、字幕、附件 |
| 蓝光文件夹 | `probe.json`（第一个 m2ts）、`bdinfo.json`、截图、内嵌图片、章节图、字幕（按 BDInfo 的流序号）、附件 |
| 音频 | `probe.json`、`[audio.tags]`、`audio.lufs`、`audio.embedded_lyrics`（为 true 时要 `lyrics.lrc`）；有内嵌封面时要 `images/primary.*` |
| CBZ | `book.page_count`、`book.comic_info`、`book.comic_book_info`、`book.cover`，以及声明为 true 的文件 |
| CBR / CB7 / CBT | `book.page_count`、`book.cover` |
| PDF | `book.page_count` |
| EPUB | `book.opf`、`book.cover`，以及声明为 true 的文件 |
| 照片 | `[photo]`，至少要有宽和高 |

需要哪些章节图和 trickplay，取决于服务器配置（`DummyChapterDuration`、`TrickplayOptions`）。修改这些配置后，旧的声明可能不再完整。

## `meta.toml`

```toml
version = 1
kind = "video"            # video、audio、dvd、bluray、book、photo、album

[source]
size = 939109             # 文件必填；与实际大小不符即拒绝
modified = 2026-10-19T09:18:12Z   # 可选；声明时必须与文件修改时间相差 2 秒以内

[video]
timestamp = "valid"       # none、zero、valid；仅 ts/m2ts
runtime_ticks = 0         # 仅 DVD

[[video.trickplay]]
width = 320
height = 180              # 单张缩略图的高度
tile_width = 10
tile_height = 10
thumbnail_count = 3
interval = 10000          # 毫秒
bandwidth = 474

[audio]
lufs = -14.5
embedded_lyrics = false

[audio.tags]              # ATL 报告的原始值；多值字段写成数组
title = "Song"
album = "Album"
artists = ["A", "B"]
album_artists = ["A"]
composers = []
genres = ["Rock"]
year = 2001
track = 3
disc = 1
date = 2001-05-01T00:00:00Z
description = ""
comment = ""
publisher = ""
id3v1_only = false

[audio.tags.additional]   # ATL 的 AdditionalFields，例如 ReplayGain、MusicBrainz、UFID
REPLAYGAIN_TRACK_GAIN = "-6.5 dB"

[book]
page_count = 24
comic_info = true
comic_book_info = false
opf = false
cover = true

[photo]
width = 4000
height = 3000
camera_make = "..."
orientation = "rightTop"  # ImageOrientation 的名称
# 其余：camera_model、rating、comment、title、date_taken、genres、keywords、software、
#       exposure_time、focal_length、latitude、longitude、altitude、iso_speed、aperture、shutter_speed

[album]                   # 仅 __album__
lufs = -9.25
```

未知的键会导致拒绝，避免拼错的键让某个值悄悄缺失。

## 生成工具

`src/Jellyfin.TrueCache.Tool` 直接复用服务器的探测和提取代码（MediaEncoder、SubtitleEncoder、AttachmentExtractor、trickplay 拼图、ATL 标签读取、书籍和照片读取），生成的内容与服务器自己提取的一致。请在文件还在本地磁盘上时运行，然后连同 `__true_cache__` 一起上传。

```sh
dotnet run --project src/Jellyfin.TrueCache.Tool -c Release -- generate --ffmpeg /usr/lib/jellyfin-ffmpeg/ffmpeg --server-config /etc/jellyfin Movies/Film/Film.mkv
dotnet run --project src/Jellyfin.TrueCache.Tool -c Release -- album  --ffmpeg ... Music/Album
dotnet run --project src/Jellyfin.TrueCache.Tool -c Release -- validate --server-config /etc/jellyfin Movies/Film/Film.mkv
```

- `--server-config` 读取服务器的 `system.xml` 和 `encoding.xml`，保证 trickplay、章节和编码设置与服务器一致；也可以用 `--trickplay-widths`、`--trickplay-interval`、`--trickplay-tile`、`--dummy-chapters` 单独指定。
- 生成后会用服务器同一套规则校验。全部完整时退出码为 0，有不完整的为 1，命令行错误为 2。
- 已有缓存的资源默认跳过，`--force` 重新生成。
- 条目先写入 `__true_cache__` 中以 `.` 开头的临时目录（服务器忽略它），校验完整后才替换正式条目。生成失败、校验不通过或被中断时，原有条目（或没有条目的状态）保持不变，服务器不会看到写了一半的条目。

## 运行时行为

- **扫描**：入库和拒绝发生在媒体库扫描解析文件时；新增、补齐或删除缓存（包括整个 `__true_cache__` 目录的移入、移出）后，扫描（或文件监控触发的刷新）会让对应资源入库或移出。
- **刷新**：修改 `meta.toml` 会让条目在下次刷新时重新读取声明。只在子目录里增删文件不会改变 `meta.toml` 和条目目录的修改时间，因此有效条目的校验结果会一直保留，直到这两者之一变化，或者调用 `POST /TrueCache/Validate?path=` 强制重新校验。被拒绝的条目每 10 秒会重新检查一次。
- **服务方式**：截图、内嵌图片、封面以流的形式交给服务器保存（服务器保存后会删除按路径交给它的文件，以流交付可以保护缓存文件）。章节图和 trickplay 拼图直接从缓存目录提供，服务器不会移动或清理它们；删除条目时，服务器只删除它自己目录里的数据。字幕和附件直接从缓存读取；附件会复制一份到服务器的附件目录，供烧录字幕时的 ffmpeg 使用。

## 仍会读取的情况

- 直接播放、下载、转码、Remux：按设计会读取原文件。蓝光文件夹播放时还会解析 BDMV 结构文件。
- 照片：照片本身就是它的主图，展示和缩放时会读取。
- `.strm`：会读取这个文本文件以得到目标地址。
- 外挂文件（外挂字幕、外挂音轨、歌词、NFO、图片）是独立的文件，按原有方式读取；外挂音轨可以有自己的 `__true_cache__` 条目。
- 第三方插件（例如字幕下载插件的哈希计算、片头检测）不在覆盖范围内。

## 管理接口

| 方法与路径 | 作用 |
| --- | --- |
| `GET /TrueCache/Rejected` | 被拒绝的资源及原因，按时间倒序 |
| `POST /TrueCache/Validate?path=` | 立即重新校验一个资源 |

两者都需要管理员权限。

## 测试

```sh
dotnet test tests/Jellyfin.MediaEncoding.Tests -c Release --filter FullyQualifiedName~TrueCache
dotnet test tests/Jellyfin.Providers.Tests -c Release --filter FullyQualifiedName~TrueCache
dotnet test tests/Jellyfin.MediaEncoding.Hls.Tests -c Release --filter FullyQualifiedName~TrueCache
dotnet test tests/Jellyfin.Server.Implementations.Tests -c Release --filter FullyQualifiedName~TrueCache
```
