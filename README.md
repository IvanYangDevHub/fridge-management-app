# 鮮日子 Fresh Days

鮮日子是家庭冰箱管理 MVP。使用者可以記錄食材、查看到期狀態、取得簡單料理建議，並讓已訂閱的 LINE 使用者收到到期摘要。

目前版本適合單一家庭展示與開發測試。食材和採買清單由後端儲存在 SQLite；多人使用時尚未提供個人帳號、家庭成員權限及即時畫面更新。

## 功能

- 新增食材，記錄數量、單位、分類、保存位置與到期日；部分食材名稱可自動帶入保存期限建議。
- 查看冷藏、冷凍與常溫庫存，依到期風險排序；標記食材用完或刪除。
- 根據庫存產生規則式料理建議，並將缺少的材料加入採買清單。
- LINE 使用者可用家庭配對碼訂閱，查詢到期食材或接收每日提醒。

保存期限與料理內容都是簡單規則產生的建議。料理卡目前可能列出已過期食材；請依食材實際狀態與包裝標示判斷，勿食用不安全的食物。

## 技術架構

```text
React + TypeScript + Vite
          │ /api
          ▼
ASP.NET Core 10 後端 ── SQLite
          │
          ├── LINE Webhook：接收與驗證事件
          └── 背景工作：檢查到期食材並呼叫 LINE API
```

開發時前後端分別運行；Vite 將 `/api` 請求代理到 `http://localhost:3000`。LINE 直接呼叫後端的 Webhook，不經由 React 前端。

```text
frontend/                        React 前端、Vite 設定與 package-lock.json
backend/src/FreshDays/           ASP.NET Core API、SQLite 與 LINE 服務
backend/tests/FreshDays.Tests/   後端單元與整合測試
LINE_WEBHOOK.md                  LINE Channel 與 Webhook 設定指南
SPEC.md                          產品需求與實作狀態
.env.example                     環境變數名稱範本
```

## 開發環境需求

- .NET 10 SDK。使用 `dotnet --version` 檢查。
- Node.js 與 npm。依目前的前端 lockfile，Vite 需要 Node.js `^20.19.0` 或 `>=22.12.0`；可使用 `node --version` 和 `npm --version` 檢查。
- PowerShell、可用的終端機，以及現代瀏覽器。以下指令以 Windows PowerShell 為例。
- 若要實際收發 LINE 訊息，需要 LINE Official Account、Messaging API Channel 與可公開存取的 HTTPS Webhook 網址。

## 環境變數

後端啟動時必須提供前三個值。所有 LINE 憑證都應由後端或部署平台管理，不要放進前端、提交至 Git，或貼到公開對話。

| 變數 | 必填 | 說明 |
| --- | --- | --- |
| `LINE_CHANNEL_SECRET` | 是 | Messaging API Channel 的 secret，用來驗證 Webhook 簽章。 |
| `LINE_CHANNEL_ACCESS_TOKEN` | 是 | 後端呼叫 LINE Reply／Push API 的 token。 |
| `HOUSEHOLD_CODE` | 是 | 自行設定的家庭配對碼，用於網站連線與 LINE 訂閱；建議使用長且隨機的值。 |
| `SQLITE_PATH` | 否 | SQLite 檔案路徑，預設為目前工作目錄下的 `freshdays.db`；部署時應使用持久化磁碟路徑。 |
| `PORT` | 否 | 指定時後端監聽 `0.0.0.0` 的該埠；未指定時預設為 `http://localhost:3000`。 |
| `ASPNETCORE_URLS` | 否 | 指定 ASP.NET Core 監聽網址；設定後優先於上述預設及 `PORT`。 |

`.env.example` 是欄位範本，後端**不會自動載入 `.env`**。PowerShell 的 `$env:` 設定只存在於目前終端機工作階段。LINE token、secret 的取得與輪替方式見 [LINE_WEBHOOK.md](LINE_WEBHOOK.md)。

## 本機啟動

先在專案根目錄開啟第一個 PowerShell 視窗。填入自己的 Channel 憑證和家庭配對碼，再啟動後端：

```powershell
$env:LINE_CHANNEL_SECRET = '你的 Channel secret'
$env:LINE_CHANNEL_ACCESS_TOKEN = '你的 Channel access token'
$env:HOUSEHOLD_CODE = '你自訂的家庭配對碼'
$env:SQLITE_PATH = './freshdays.db'

dotnet run --project backend/src/FreshDays
```

後端正常啟動後會顯示監聽網址。另開視窗，在專案根目錄檢查健康狀態：

```powershell
Invoke-RestMethod http://localhost:3000/health
```

預期回應是 `ok`。若你設定了不同的 `PORT` 或 `ASPNETCORE_URLS`，也要調整 [Vite 代理設定](frontend/vite.config.ts) 或提供對應的反向代理。

再開啟另一個 PowerShell 視窗，在專案根目錄啟動前端：

```powershell
cd frontend
npm ci
npm run dev
```

開啟 Vite 顯示的網址，預設是 `http://localhost:5173`。輸入與後端相同的家庭配對碼後，即可操作食材與採買清單。首次啟動會建立 SQLite 資料庫與資料表。停止任一服務可在該終端機按 `Ctrl+C`。

本機若尚未完成 LINE 設定，可暫時以測試用非空字串啟動後端並測試網站；但 LINE 回覆與推送必須使用有效憑證及公開 Webhook 才能驗證。

## 操作與驗證

1. 連線家庭冰箱後，新增四筆食材，將到期日分別設為昨天、今天、明天及四天後。
2. 確認狀態依序為「已過期」、「今天到期」、「3 天內到期」、「正常」，以及「需優先處理」的數量為 3。
3. 刷新頁面並重新連線，確認食材仍保留；使用冷藏／冷凍／常溫篩選，再測試「已用完」與刪除。
4. 查看料理建議，將缺少材料加入採買清單，勾選或移除後刷新確認保存結果。
5. 完成 LINE 設定後傳送 `ping`，再傳送 `訂閱 你的家庭配對碼` 與 `到期`；不想接收時傳送 `取消訂閱`。

後端在台灣時間 09:00 之後檢查已過期或三天內到期的食材。伺服器需持續運行；如果當時關閉，當天稍後啟動會再次檢查。每位訂閱者的當日發送狀態與 retry key 儲存在 SQLite。

## API 概覽

除健康檢查與 LINE Webhook 外，庫存與採買 API 需要 `x-household-code` 標頭。這是展示版的共享配對碼，尚不是個人帳號授權。

| 方法與路徑 | 用途 |
| --- | --- |
| `GET /health` | 回傳 `ok`。 |
| `GET /api/inventory` | 讀取食材清單。 |
| `PUT /api/inventory` | 以 JSON 陣列取代整份食材清單。 |
| `GET /api/shopping` | 讀取採買清單。 |
| `PUT /api/shopping` | 以 JSON 陣列取代整份採買清單。 |
| `POST /webhook/line` | 驗證 `x-line-signature` 後處理 LINE 事件。 |

清單端點也接受 `POST` 進行整份清單取代。不同瀏覽器若同時修改，後送出的整份清單可能覆蓋先前的變更；目前沒有衝突偵測。不要將此配對碼展示版直接當成完整的多人權限系統。

## 測試與建置

在專案根目錄執行後端測試，並輸出 Coverlet 的 Cobertura 覆蓋率報告：

```powershell
dotnet test backend/tests/FreshDays.Tests --collect:"XPlat Code Coverage"
```

命令完成後會顯示 `coverage.cobertura.xml` 的路徑。測試包括到期狀態、SQLite 持久化、API、LINE 簽章與通知重試；測試中的 LINE API 會以替身模擬，不會發送真實訊息。查看報告時請同時檢查行與分支覆蓋率，並確認涵蓋範圍是整個後端產品程式。

前端型別檢查與正式建置：

```powershell
cd frontend
npm run build
```

產物在 `frontend/dist/`；此命令不會啟動或部署後端。

## LINE 與部署

DigitalOcean Droplet 的部署準備、主機設定、LINE 驗收及更新備份步驟見 [DEPLOY_DIGITALOCEAN.md](DEPLOY_DIGITALOCEAN.md)。該指南所述的 Docker 設定尚未加入專案，須先完成指南第 1 節。

詳細的 LINE Official Account、Channel secret、access token、Webhook URL、驗證與測試流程請見 [LINE_WEBHOOK.md](LINE_WEBHOOK.md)。注意 access token 的有效期及換發；目前服務不會自動換發 token。

部署時，後端需要持續運行、可供 LINE 存取的 HTTPS 網址及持久化 SQLite 儲存空間。設定 `SQLITE_PATH` 指向持久化磁碟，只執行一個後端實例，並安排備份與通知失敗監控。React 可獨立部署為靜態網站，但生產環境仍需將同網域的 `/api` 反向代理至後端；Vite 的開發代理不會自動套用到生產環境。

## 常見問題

| 問題 | 檢查方式 |
| --- | --- |
| 後端啟動時顯示缺少設定 | 確認目前終端機已設定三個必填變數，再重新啟動。 |
| 網頁顯示配對碼不正確 | 前端輸入的值須與後端 `HOUSEHOLD_CODE` 完全相同。 |
| 網頁無法讀取資料 | 確認後端正在 `localhost:3000` 運行，且 Vite `/api` 代理指向正確埠號。 |
| 重啟後資料不見 | 確認啟動時的工作目錄與 `SQLITE_PATH`；部署環境需使用持久化磁碟。 |
| LINE Verify 失敗或收不到訊息 | 檢查公開 HTTPS URL、`/webhook/line`、簽章 secret、token 與 Use webhook 設定；參見 LINE 設定指南。 |

## 目前限制

- 家庭配對碼共用，尚未實作個人登入、多家庭隔離與完整權限管理。
- 食材與採買清單以整份陣列寫入，並行操作可能互相覆蓋；其他瀏覽器需要重新整理才會載入新資料。
- 料理與保存期限建議基於固定規則，並非食安保證；尚未串接拍照、條碼、語音或 AI 辨識。
- 正式使用前需完成資料保護、備份、部署監控，並以真實 LINE Channel 做端到端驗證。
