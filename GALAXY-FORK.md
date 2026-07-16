# Galaxy fork của OfficeCLI

Fork của [iOfficeAI/OfficeCLI](https://github.com/iOfficeAI/OfficeCLI) (Apache 2.0) cho nền tảng Galaxy Vortex.
Upstream remote: `upstream` → `iOfficeAI/OfficeCLI`. Đồng bộ upstream theo tag, mỗi lần sync phải rà lại các delta dưới đây.

## Nguyên tắc fork

- **Diff tối thiểu**: vô hiệu tại gốc bằng early-return + `#pragma warning disable CS0162`, không xoá code upstream — để rebase/sync upstream không xung đột.
- Binary chỉ được build và phân phối qua pipeline CI/compose của Galaxy, version-pinned.
- Không đổi tên nội bộ (`officecli`, namespace `OfficeCli`) — tên Galaxy nằm ở tầng service bọc ngoài (`galaxy-office-mcp`).

## Delta so với upstream (v1.0.136)

| # | File | Thay đổi | Lý do |
|---|------|----------|-------|
| 1 | `src/officecli/Program.cs` | Gỡ call `Installer.MaybeAutoInstall()` + `UpdateChecker.CheckInBackground()`; lệnh nội bộ `__update-check__` thành no-op có thông báo | Chặn supply-chain: binary không tự copy mình đi nơi khác, không tự tải bản mới từ mirror ngoài |
| 2 | `src/officecli/Core/UpdateChecker.cs` | `CheckInBackground()` early-return vĩnh viễn | Như trên, defense-in-depth cho mọi call site (kể cả vòng lặp hàng giờ trong `McpServer.cs`) |
| 3 | `src/officecli/Core/Installer.cs` | `MaybeAutoInstall()` early-return vĩnh viễn | Cài đặt chỉ qua `officecli install` tường minh |
| 4 | `src/officecli/Core/KatexAssets.cs` | Mirror slot đọc env `OFFICECLI_ASSET_MIRROR_BASE`; unset → jsdelivr CDN | HTML preview không được nhúng URL VPS của upstream (`d.officecli.ai`) |
| 5 | `src/officecli/Core/Diagram/MermaidImageRenderer.cs` | Như trên cho mermaid.js | Như trên |
| 6 | `src/officecli/Handlers/Word/WordHandler.HtmlPreview.cs` | Google Fonts trực tiếp thay proxy upstream | Như trên |
| 7 | `NOTICE` | Thêm mục Galaxy modifications | Nghĩa vụ Apache 2.0 §4 |

## Env vars của fork

| Env | Ý nghĩa |
|-----|---------|
| `OFFICECLI_ASSET_MIRROR_BASE` | Prefix static Galaxy host serve `katex-<ver>/…` + `mermaid-<ver>.min.js` (vd `https://static.skyplatform.net/officecli-assets`). Unset = dùng jsdelivr. |
| `OFFICECLI_NO_AUTO_RESIDENT=1` | (Upstream sẵn có) tắt resident auto-spawn — **bắt buộc** trong container đa user của galaxy-office-mcp. |

## Checklist khi sync upstream

1. `git fetch upstream && git merge upstream/main` (hoặc rebase theo tag release).
2. Grep xác nhận không có tham chiếu mirror mới: `grep -rn "d.officecli.ai" src/` — chỉ được còn trong comment/`UpdateChecker.PrimaryBase` (dead path).
3. Grep xác nhận không có call site mới của `CheckInBackground`/`MaybeAutoInstall` ngoài các điểm đã vô hiệu.
4. Build + chạy `officecli --version` trong container, xác nhận không có network call ra ngoài lúc khởi động (ngoài lệnh người dùng gọi).
