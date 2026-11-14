# 鮮日子：DigitalOcean Droplet 部署指南

本指南記錄單一家庭展示版的部署方案：一台 Ubuntu Droplet、Docker Compose、React 靜態網站、ASP.NET Core 後端與持久化 SQLite。正式環境從空資料庫開始，不搬移本機資料。將下文的 `freshdays.example.com` 換成你擁有的網域或子網域。

> **目前狀態：尚未加入 Dockerfile、Compose 與 Caddy 設定。** 先完成第 1 節的專案準備並驗證映像檔，再依序操作 Droplet。只把目前的 repository 複製到主機，還不能執行下文的 `docker compose up`。

## 1. 準備專案部署設定

在 repository 加入後端 Dockerfile、前端 Dockerfile、`compose.yaml`、`Caddyfile` 及適當的 `.dockerignore`：

1. 後端以 .NET 10 SDK 映像建置 `backend/src/FreshDays/FreshDays.csproj`，以 .NET 10 ASP.NET runtime 映像執行 `FreshDays.dll`。設定 `ASPNETCORE_URLS=http://0.0.0.0:8080`，只在 Compose 內部開放 `8080`，不要將後端埠對外發布。
2. 前端以 Node.js 建置：在 `frontend/` 執行 `npm ci` 與 `npm run build`，再將 `frontend/dist/` 放入 Caddy 映像作為網站根目錄。Vite 的 `/api` 開發代理不會隨正式版一起部署。
3. Caddy 以 `freshdays.example.com` 為站台名稱；將 `/api/*`、`/webhook/line`、`/health` **保留原始路徑**轉送至 `backend:8080`。其他路徑提供前端檔案，找不到檔案時回傳 `index.html`。不要將 `/webhook/line` 導向 React。
4. Compose 只讓 Caddy 對外發布 `80`、`443`；將主機 `/opt/freshdays/data` 掛載至後端 `/data`，設定 `SQLITE_PATH=/data/freshdays.db`；持久化 Caddy 的資料目錄以保存 HTTPS 憑證。後端只啟動**一個**實例，避免多個每日提醒背景工作同時執行。
5. 在本機先驗證後端測試及前端建置：

   ```powershell
   dotnet test backend/tests/FreshDays.Tests
   npm --prefix frontend ci
   npm --prefix frontend run build
   ```

部署設定完成後，再檢查 `docker compose config` 及兩個映像檔能否成功建置。參考 [.NET 容器文件](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/docker/building-net-docker-images?view=aspnetcore-10.0)、[Caddy SPA 設定](https://caddyserver.com/docs/caddyfile/patterns)與[Caddy 自動 HTTPS](https://caddyserver.com/docs/automatic-https)。

## 2. 建立 Droplet、DNS 與防火牆

1. 在 Windows 建立 SSH 金鑰，將**公鑰**加入 DigitalOcean 帳號；建立 Droplet 時選擇 SSH key 登入。私鑰留在自己的電腦，不要上傳到 repository。操作方式見 [DigitalOcean SSH 金鑰指南](https://docs.digitalocean.com/products/droplets/how-to/add-ssh-keys/)。
2. 建立 Ubuntu 24.04 Droplet。建議新加坡區域、至少 2 vCPU／4 GB RAM，以便在主機上建置兩個容器。建立時開啟自動備份。
3. 設定 DigitalOcean Cloud Firewall：`22/TCP` 僅允許管理者的 IP；`80/TCP` 與 `443/TCP` 允許公開連線，並將防火牆套用到 Droplet。[防火牆設定文件](https://docs.digitalocean.com/products/networking/firewalls/how-to/create/)
4. 在你的 DNS 管理介面新增 `freshdays` 的 A 記錄，值為 Droplet 的公開 IPv4。若有指向其他主機的 AAAA 記錄，也要更新或移除。確認 DNS 指向正確主機，且 `80`、`443` 可連線後，再啟動 Caddy 讓它取得公開 HTTPS 憑證。[Caddy HTTPS 前置條件](https://caddyserver.com/docs/quick-starts/https)

## 3. 在 Droplet 啟動服務

1. 使用 SSH 連入 Droplet，依 [Docker 官方 Ubuntu 安裝步驟](https://docs.docker.com/engine/install/ubuntu/)安裝 Docker Engine、Buildx 與 Compose plugin。執行 `docker compose version` 確認安裝成功。
2. 將**已包含第 1 節部署設定**的 repository 放在 `/opt/freshdays`，建立 `/opt/freshdays/data`，確認後端容器可寫入該目錄。
3. 在主機的 `/opt/freshdays/.env` 填入以下值，並設定只有管理者可讀。`.env` 已被本專案的 `.gitignore` 排除；後端不會自行讀取它，必須由 Compose 注入環境變數。

   ```dotenv
   LINE_CHANNEL_SECRET=<你的 Messaging API Channel secret>
   LINE_CHANNEL_ACCESS_TOKEN=<你的 Messaging API Channel access token>
   HOUSEHOLD_CODE=<自訂的長且隨機的家庭配對碼>
   SQLITE_PATH=/data/freshdays.db
   ASPNETCORE_URLS=http://0.0.0.0:8080
   ```

4. 將 `Caddyfile` 中的 `freshdays.example.com` 換成實際網域。在 `/opt/freshdays` 執行：

   ```bash
   sudo docker compose config
   sudo docker compose up -d --build
   sudo docker compose ps
   sudo docker compose logs --tail=100
   ```

   `docker compose config` 可能在終端機顯示環境變數值；不要將輸出貼到公開地方。若建置或啟動失敗，先依服務名稱查看 `sudo docker compose logs <服務名稱>`。

## 4. 驗收網站與 LINE Webhook

1. 開啟 `https://freshdays.example.com/health`，應回傳 `ok`。再開啟網站，輸入與後端一致的家庭配對碼，新增食材並重新整理，確認資料仍在。
2. 在 LINE Developers Console 的 Messaging API 分頁，將 Webhook URL 設為 `https://freshdays.example.com/webhook/line`，按 **Verify**；成功後開啟 **Use webhook**。本專案真正的 route 是 `POST /webhook/line`，定義在 `backend/src/FreshDays/Program.cs`。
3. 加入官方帳號，確認收到 `follow` 歡迎訊息；傳送 `ping`，應收到 `pong 🥬 鮮日子 LINE Webhook 已連線。`。再傳送 `訂閱 <家庭配對碼>` 與 `到期`，確認訂閱及查詢正常。詳細 LINE 設定見 [LINE_WEBHOOK.md](LINE_WEBHOOK.md)。
4. 重新啟動服務後再查詢網站資料與 LINE 訂閱狀態，確認 SQLite 使用的是持久化掛載路徑。若沒有資料，先檢查 `SQLITE_PATH` 和掛載設定。

## 5. 更新與備份

- 更新時先備份資料，取得已驗證的程式版本，再於 `/opt/freshdays` 執行 `sudo docker compose up -d --build`。更新後重新檢查 `/health`、網站操作與 LINE 回覆。
- 保持 [Droplet 自動備份](https://docs.digitalocean.com/products/backups/how-to/enable/)開啟。更新前另用 [SQLite 線上備份功能](https://sqlite.org/backup.html)產生資料庫副本，複製到 Droplet 之外，並定期測試還原。不要在資料庫使用中直接複製 `.db` 檔作為唯一備份。
- 注意 LINE Channel access token 的有效期限；目前程式不會自動換發。若每日推送失敗，檢查後端日誌、token 和 SQLite 的持久化狀態。

完成標準：網站與 `/health` 可透過 HTTPS 存取、LINE Verify 成功、`follow`／`ping`／訂閱流程正常，且服務重啟後資料仍保留。
