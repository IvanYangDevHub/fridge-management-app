# LINE Messaging API Webhook 設定

本專案的 ASP.NET Core 10 後端提供 `POST /webhook/line`、`GET /health`、`/api/inventory` 與 `/api/shopping`，並在台灣時間每日 09:00 後檢查到期食材。React 前端獨立運行，後端使用 SQLite 保存資料。

## 1. 建立官方帳號並取得 Channel 憑證

1. 到 [LINE Official Account Manager](https://manager.line.biz/) 建立 LINE 官方帳號，或選用既有帳號，並為該帳號啟用 **Messaging API**。啟用時選擇管理它的 Provider；這一步會建立 Messaging API Channel。現行流程不能直接從 Developers Console 建立此類 Channel。
2. 使用相同的登入帳號進入 [LINE Developers Console](https://developers.line.biz/console/)，選擇剛才的 Provider 與 Messaging API Channel。
3. 在 **Basic settings** 複製 **Channel secret**，設定為 `LINE_CHANNEL_SECRET`。後端使用它驗證 Webhook 原始請求的簽章。
4. 取得此 Channel 的 **Channel access token**，設定為 `LINE_CHANNEL_ACCESS_TOKEN`。後端使用它呼叫 Reply API 與 Push API。LINE 建議使用可設定期限的 v2.1 token；它最長有效 30 天，而本程式尚未自動換發，請在到期前安排更新。若使用 Console 的 long-lived token，也應妥善保管與定期檢視。不要把 LINE Login 的 access token 誤用在 Messaging API。
5. 自行產生一組長且隨機的 `HOUSEHOLD_CODE`，供 React 網頁連線與 LINE 訂閱配對。此值不是 LINE 發給你的憑證。三個秘密值都不要放進 Git、前端程式或對話訊息。

LINE 官方步驟：[開始使用 Messaging API](https://developers.line.biz/en/docs/messaging-api/getting-started/)、[建立 Bot](https://developers.line.biz/en/docs/messaging-api/building-bot/)、[Channel access token](https://developers.line.biz/en/docs/basics/channel-access-token/)。

## 2. 本機啟動

在 PowerShell 執行（將值替換成你自己的憑證）：

```powershell
$env:LINE_CHANNEL_SECRET = '你的 Channel secret'
$env:LINE_CHANNEL_ACCESS_TOKEN = '你的 Channel access token'
$env:HOUSEHOLD_CODE = '請自訂一組長且隨機的家庭配對碼'
$env:SQLITE_PATH = './freshdays.db'
dotnet run --project backend/src/FreshDays
```

另一個終端機可測試：

```powershell
Invoke-WebRequest http://localhost:3000/health
dotnet test backend/tests/FreshDays.Tests --collect:"XPlat Code Coverage"
```

`health` 應回傳 `ok`。React 開發網頁位於 `http://localhost:5173`；另開視窗在 `frontend/` 執行 `npm install` 與 `npm run dev`。直接向 Webhook 傳送沒有正確簽章的請求會得到 HTTP 401，這是預期行為。`.env.example` 列出需要的環境變數；若自行建立 `.env`，後端不會自動載入它，仍需透過部署平台設定或自行載入環境變數。

## 3. 提供公開 HTTPS 網址

LINE 無法存取 `localhost`。將 ASP.NET Core 後端部署到可公開存取、具有效 TLS 憑證且會持續運作的主機，啟動命令可使用 `dotnet run --project backend/src/FreshDays`。在主機設定 `LINE_CHANNEL_SECRET`、`LINE_CHANNEL_ACCESS_TOKEN`、`HOUSEHOLD_CODE`、`SQLITE_PATH`，以及主機要求的 `PORT`。`SQLITE_PATH` 必須指向持久化磁碟上的檔案，例如 `/var/data/freshdays.db`。開發測試可用 HTTPS tunnel 暫時公開本機 `localhost:3000`。確認公開網址的 `/health` 回傳 `ok`，再前往下一步。

Webhook URL 格式為 `https://你的後端網域/webhook/line`。React 生產版會呼叫同網域的 `/api`；若將前端部署到另一個網域，須額外設定反向代理。開發時的 Vite 代理設定不會自動套用到生產部署。

## 4. LINE Console 啟用

1. 在 Channel 的 **Messaging API** 分頁，將 **Webhook URL** 設為上述 HTTPS URL。
2. 按 **Verify**。LINE 會傳送帶有效簽章且 `events` 為空的 POST；成功時應顯示 Success。
3. 開啟 **Use webhook**。
4. 用 Messaging API 分頁的 QR code 加好友，傳送 `ping`，應收到 `pong 🥬 鮮日子 LINE Webhook 已連線。`。
5. 在 React 網頁輸入家庭配對碼並連線，新增食材後會寫入後端庫存。
6. 在 LINE 傳送 `訂閱 你的家庭配對碼`。收到訂閱成功訊息後，傳送 `到期` 可立即查看目前的到期摘要。傳送 `取消訂閱` 可停止通知。
7. 伺服器持續運作時，每天台灣時間 09:00 後會檢查一次庫存；若有已過期或三天內到期食材，就向已訂閱者推送摘要。若 09:00 時伺服器未運作，啟動後也會補做當日檢查。
8. 若官方帳號的自動回覆也會回訊，可在 LINE Official Account Manager 檢查回應設定，避免測試時看到重複訊息。

## 故障排查

| 現象 | 檢查 |
| --- | --- |
| Verify 無法連線 | URL 必須是公開 HTTPS，憑證有效；確認 `/health` 可從外網存取，且 `/webhook/line` 路由正確。 |
| HTTP 401 | Channel secret 與 Webhook 所屬 Channel 不一致，或代理伺服器改動了原始 body。 |
| Verify 成功但 `ping` 沒回覆 | 已開啟 Use webhook、access token 正確且未過期，並檢查伺服器的 LINE reply HTTP 狀態紀錄。 |
| 訊息重複 | 檢查官方帳號既有的自動回應設定。 |

伺服器在 `SQLITE_PATH` 指定的 SQLite 檔保存庫存、採買清單、LINE 訂閱者與每位訂閱者每日的發送紀錄。部署主機必須提供持久化磁碟，且只能啟動一個伺服器實例；一般無狀態 serverless 平台不適合此版本。這是單一家庭展示版，尚未提供個人登入與完整家庭權限。共用資料由後端提供，已連線的其他瀏覽器重新整理後可讀到最新內容，但畫面不會即時自動更新。請勿公開配對碼；換碼不會自動取消既有訂閱者。LINE 推送失敗時會保留每位訂閱者的 retry key 以供重試，請監控錯誤紀錄。

參考：[建立 Bot](https://developers.line.biz/en/docs/messaging-api/building-bot/)、[接收 Webhook](https://developers.line.biz/en/docs/messaging-api/receiving-messages/)、[簽章驗證](https://developers.line.biz/en/docs/messaging-api/verify-webhook-signature/)、[回覆訊息](https://developers.line.biz/en/docs/messaging-api/sending-messages/)。
