# CfTunnelTool

一個 Windows 圖形介面工具，透過 **Cloudflare Tunnel** 讓外網用你自己的網域連到這台電腦上的網站和 API，
也可以當作**遠端桌面中繼**，讓使用者用瀏覽器、通過 **2FA** 後連到這台或區網內其他電腦的遠端桌面。

- 不需要固定 IP、不用設定路由器 Port Forwarding、不用開防火牆
- Cloudflare 自動提供 HTTPS
- 遠端桌面登入要先通過 Email 驗證碼 ＋ 2FA（Authenticator App / 安全金鑰 / 生物辨識），RDP 連接埠完全不對外
- 安裝成 Windows 服務，開機自動連線
- 整個流程都有逐步引導，約 5 分鐘完成

```
瀏覽器 ──HTTPS──▶ Cloudflare ◀══ 加密通道（由內往外）══ cloudflared 服務 ──▶ localhost:80 / :5000
                    │                                         └──▶ 區網電腦:3389（瀏覽器 RDP）
                    └ Access：Email 驗證碼 ＋ 2FA
```

![部署畫面](docs/screenshots/4-deploy.png)

## 下載與安裝

1. 到 [Releases](../../releases/latest) 下載 `CfTunnelTool-<版本>-win-x64.zip`（私有 repo 需要先登入 GitHub）
2. 解壓縮後按兩下 `CfTunnelTool.exe`（單一檔案，不需要安裝 .NET）
3. 若出現「Windows 已保護您的電腦」，按「其他資訊」→「仍要執行」（程式沒有程式碼簽章）
4. Windows 詢問權限時按「是」，接著跟著精靈完成五個步驟

也可以用 GitHub CLI 下載：

```bash
gh release download --repo ad823/CfTunnelTool --pattern "*.zip"
```

zip 內附的 [使用說明.txt](docs/使用說明.txt) 有完整步驟和第一次部署的驗證清單。

## 設定精靈

| 步驟 | 內容 |
|---|---|
| 1. API Token | 按按鈕開啟 Cloudflare 頁面，依畫面上的表格建立 Token、貼上、驗證（程式會檢查權限是否足夠） |
| 2. 網域 | 從下拉選單選網域，取一個 Tunnel 名稱（預設為電腦名稱） |
| 3. 網址 | 設定「子網域 → 本機服務」，例如 `api` → `http://localhost:5000`，可以直接測試本機服務有沒有回應（只用遠端桌面可以不設） |
| 4. 遠端桌面（選用） | 加入這台或區網其他電腦、允許登入的 Email、2FA 方式、剪貼簿與檔案傳輸權限，並可一鍵啟用這台電腦的遠端桌面 |
| 5. 部署 | 按「開始部署」，自動完成 Tunnel、DNS、Zero Trust 登入政策、下載 cloudflared、安裝服務，並即時顯示進度 |

<table>
<tr>
<td><img src="docs/screenshots/1-token.png" alt="步驟 1：API Token"></td>
<td><img src="docs/screenshots/2-routes.png" alt="步驟 3：設定網址"></td>
</tr>
<tr>
<td><img src="docs/screenshots/3-rdp.png" alt="步驟 4：遠端桌面 + 2FA"></td>
<td><img src="docs/screenshots/5-done.png" alt="完成頁"></td>
</tr>
</table>

完成後：

- 程式會把自己安裝到 `C:\Program Files\CfTunnelTool\`，並在**開始功能表和桌面**建立「CfTunnelTool」捷徑
- 之後開啟捷徑會直接進入**管理畫面**，可以查看服務與 Tunnel 狀態、測試本機服務與 RDP 連接埠、開啟網址、複製給使用者的 RDP 連線資訊、重新啟動服務、修改設定、移除全部設定

![管理畫面](docs/screenshots/6-dashboard.png)

## 遠端桌面（瀏覽器 RDP ＋ 2FA）

使用者開啟連線網址後，依序通過 **Email 驗證碼 → 2FA → Windows 帳號密碼**，就能在瀏覽器裡使用遠端桌面，不需要安裝任何軟體。

**管理者需要準備：**

- API Token 額外加上三個權限：`Access: Apps and Policies`、`Access: Organizations, Identity Providers, and Groups`、`Zero Trust`（都是 Account → Edit）
- Cloudflare 帳號先啟用 Zero Trust（[one.dash.cloudflare.com](https://one.dash.cloudflare.com/)，Free 方案即可，最多 50 位使用者）
- 被開放的電腦：Windows 專業版以上、已啟用遠端桌面、固定 IP（或 DHCP 保留）

**部署時工具會自動建立：**

| Cloudflare 資源 | 用途 |
|---|---|
| Email 一次性驗證碼登入、組織的獨立 MFA | 使用者登入與 2FA |
| Tunnel 私有網路路由（每台電腦一條 `/32`） | 讓 Cloudflare 透過這台電腦連到區網電腦 |
| 基礎架構目標（Targets） | 每台要開放的電腦 |
| DNS `rdp.網域`（Proxied A 紀錄） | 瀏覽器 RDP 的網址 |
| Access 政策 | 允許的 Email、2FA 方式與重新驗證間隔、剪貼簿與檔案傳輸方向 |
| Access 應用程式（瀏覽器 RDP） | 把以上組合起來 |

移除或停用遠端桌面時，只會刪除設定檔裡記錄、由這台電腦建立的資源。

**遠端登入密碼：** Windows 不允許空白密碼的帳號遠端登入。精靈的「這台電腦的遠端登入密碼」可以幫本機帳號設定密碼、加入 Remote Desktop Users 群組，並**保留開機自動登入**。自動登入的密碼存在 Windows 的 LSA 機密，做法和微軟 Sysinternals Autologon 相同，不會以明文寫進登錄檔。遠端登入時帳號輸入 `電腦名稱\帳號`。Microsoft 帳號、網域帳號的密碼無法在這裡修改。

**限制：** 剪貼簿單次 500 KB、不能複製圖片、沒有聲音、檔案傳輸為 Cloudflare 的 Beta 功能。需要聲音或多螢幕時，請改用 Cloudflare WARP ＋ Windows 遠端桌面連線。

## 安全設計

- **不會覆寫你現有的 DNS**：網址已有 A/AAAA 紀錄時會停下來提醒，指向其他 Tunnel 的紀錄會先詢問
- **不會誤用別台電腦的 Tunnel**：同名 Tunnel 已存在時會先詢問；移除時只刪除這台電腦建立的 Tunnel 與 DNS
- **API Token 以 Windows DPAPI 加密**存在 `C:\ProgramData\CfTunnelTool\token.dat`，只有同一個 Windows 使用者能解開（也可以選擇不記住）
- 服務使用的是 Cloudflare 產生的 Tunnel Token，只能連這一條 Tunnel，權限比 API Token 小
- **遠端桌面不對外開任何連接埠**：沒通過 Access 的 Email 驗證與 2FA，連 RDP 登入畫面都看不到
- 當作遠端桌面中繼的電腦，建議部署時**不要勾選記住 Token**，避免電腦被入侵時影響整個網域

> 網站和 API 公開後任何人都能連線，請務必做好登入驗證。
> 只給內部使用的網址，可以在 Cloudflare 後台 → Zero Trust → Access → Applications 加上登入保護。

## 檔案位置

| 位置 | 內容 |
|---|---|
| `C:\ProgramData\CfTunnelTool\config.json` | 網域、Tunnel、網址設定（不含密鑰） |
| `C:\ProgramData\CfTunnelTool\token.dat` | 加密後的 API Token |
| `C:\Program Files\CfTunnelTool\CfTunnelTool.exe` | 管理程式 |
| `C:\Program Files\cloudflared\cloudflared.exe` | Cloudflare 官方連線程式（從官方 GitHub 下載） |
| Windows 服務 `Cloudflared` | 開機自動執行的連線服務 |

## 開發

需要 .NET 9 SDK。

| 路徑 | 內容 |
|---|---|
| `CfTunnelTool/Core/` | 與介面無關的邏輯：Cloudflare API、部署流程、cloudflared 服務、設定儲存、自我安裝 |
| `CfTunnelTool/UI/` | 設定精靈、管理畫面、共用元件 |
| `CfTunnelTool/UI/Steps/` | 精靈的每一個步驟 |
| `docs/` | 使用說明（會放進部署包）與截圖 |
| `.github/workflows/release.yml` | 自動建置與發布 Release |

本機建置單一執行檔（產生 `CfTunnelTool/publish/CfTunnelTool.exe`，約 48 MB）：

```bat
CfTunnelTool\publish.cmd
```

截圖與自我測試（只有 Debug 版有這兩個功能；使用假資料，不會修改系統或 Cloudflare 設定）：

```bat
dotnet build CfTunnelTool
CfTunnelTool\bin\Debug\net9.0-windows\CfTunnelTool.exe --screenshots docs\screenshots
CfTunnelTool\bin\Debug\net9.0-windows\CfTunnelTool.exe --selftest %TEMP%\cftunnel-selftest.txt
```

自我測試涵蓋：本機遠端桌面狀態偵測、設定驗證規則、送給 Cloudflare 的 JSON 結構、API 錯誤處理。

## 發布新版本

推送版本 tag 後，GitHub Actions 會自動建置並建立 Release，附上 zip 部署包和 SHA256：

```bash
git tag v1.0.1
git push origin v1.0.1
```

也可以到 GitHub 的 **Actions → Release → Run workflow** 輸入版本號手動發布。程式內顯示的版本號會跟著 tag 一起更新。
