# LINE Messaging API Webhook 設定

本專案的 Webhook 位址為 `POST /webhook/line`，健康檢查為 `GET /health`。Node 伺服器提供 API 與 LINE Webhook；React 前端獨立運行。後端不需要安裝 npm 套件。建議 Node.js 18 以上。

## 1. 建立 LINE Channel

1. 登入 [LINE Developers Console](https://developers.line.biz/console/)，建立 Provider 與 Messaging API channel，並連結 LINE Official Account。
2. 在 Channel 的 **Basic settings** 複製 **Channel secret**。
3. 在 **Messaging API** 分頁取得 **Channel access token**。建議使用可設定期限的 v2.1 token。
4. 不要將 secret 或 token 放進 Git、網頁 JavaScript 或對話訊息。

## 2. 本機啟動

在 PowerShell 執行（將值替換成你自己的憑證）：

```powershell
$env:LINE_CHANNEL_SECRET = '你的 Channel secret'
$env:LINE_CHANNEL_ACCESS_TOKEN = '你的 Channel access token'
$env:HOUSEHOLD_CODE = '請自訂一組長且隨機的家庭配對碼'
$env:PORT = '3000'
node line-webhook.mjs
```

另一個終端機可測試：

```powershell
Invoke-WebRequest http://localhost:3000/health
node --test line-webhook.test.mjs
```

`health` 應回傳 `ok`。React 開發網頁位於 `http://localhost:5173`；另開視窗在 `frontend/` 執行 `npm install` 與 `npm run dev`。直接向 Webhook 傳送沒有正確簽章的請求會得到 HTTP 401，這是預期行為。`.env.example` 列出需要的環境變數；若自行建立 `.env`，Node 不會自動載入它，仍需透過部署平台設定或自行載入環境變數。

## 3. 提供公開 HTTPS 網址

LINE 無法存取 `localhost`。將這個 Node 伺服器部署到可公開存取、具有效 TLS 憑證的主機，啟動命令為 `node line-webhook.mjs`，並在主機上設定上述兩個環境變數及平台指定的 `PORT`。開發時也可用支援 HTTPS 的 tunnel 將本機 `localhost:3000` 暫時公開。確定公開網址的 `/health` 回傳 `ok`，再前往下一步。

正式 Webhook URL 格式：`https://你的網域/webhook/line`。目前只提供接收服務；部署平台與網域可依你的現有帳號選擇。

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

伺服器在 `line-data.json` 保存同步的庫存、訂閱者與當日發送紀錄。部署主機必須提供持久化磁碟，且只能啟動一個伺服器實例；一般無狀態 serverless 平台不適合此版本。此功能是單一家庭展示版，尚未提供完整的多家庭權限與跨瀏覽器庫存同步。請勿公開配對碼；換碼時需重新訂閱。每日推送在 LINE API 失敗時會重試，部分收件者可能收到重複訊息。

參考：[建立 Bot](https://developers.line.biz/en/docs/messaging-api/building-bot/)、[接收 Webhook](https://developers.line.biz/en/docs/messaging-api/receiving-messages/)、[簽章驗證](https://developers.line.biz/en/docs/messaging-api/verify-webhook-signature/)、[回覆訊息](https://developers.line.biz/en/docs/messaging-api/sending-messages/)。
