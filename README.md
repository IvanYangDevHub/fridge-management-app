# 鮮日子

## React 前後端分離版

前端位於 `frontend/`，使用 React、TypeScript 與 Vite；後端是根目錄的 `line-webhook.mjs`，提供 JSON API、LINE Webhook 和每日到期通知。兩者獨立運行，開發時 Vite 將 `/api` 代理到後端。根目錄的原生 `index.html`、`app.js`、`styles.css` 保留作舊版參照。

第一個 PowerShell 視窗：

```powershell
$env:LINE_CHANNEL_SECRET = '你的 Channel secret'
$env:LINE_CHANNEL_ACCESS_TOKEN = '你的 Channel access token'
$env:HOUSEHOLD_CODE = '你的家庭配對碼'
node line-webhook.mjs
```

第二個視窗：

```powershell
cd frontend
npm install
npm run dev
```

開啟 `http://localhost:5173`，輸入配對碼。後端會在 `line-data.json` 保存資料。首次連線且伺服器庫存為空時，可按「匯入舊版瀏覽器資料」。部署時前端可放在靜態網站，將 `/api` 反向代理到後端；LINE Webhook 指向後端的 `/webhook/line`。目前的配對碼只適合單一家庭展示，正式多人使用需加入個人登入與家庭授權。

## LINE Messaging API Webhook

Webhook 接收端已建於 `line-webhook.mjs`。它驗證原始請求的 `x-line-signature`，接受 LINE Console 的空事件測試，並在加入好友或傳送 `ping` 時以 Reply API 回覆。網頁可將庫存同步到伺服器；LINE 訂閱者可查詢到期品項並接收每日提醒。設定與公開 HTTPS 步驟見 [LINE_WEBHOOK.md](LINE_WEBHOOK.md)。

以下是原生 JavaScript 舊版操作說明。React 版以後端資料為準。

目前可操作的功能：食材新增、分類與保存期限建議、到期風險排序、位置篩選、標記用完、刪除、料理建議、缺少材料採買清單，以及可複製的 LINE 提醒草稿。

## 本機啟動

在專案根目錄執行：

```powershell
python -m http.server 4173
```

接著開啟 `http://localhost:4173`。

若系統沒有 Python，可直接以瀏覽器開啟 `index.html`；新增、查看、刪除與 localStorage 保存均可運作。

## 驗證流程

1. 新增四個食材，分別將到期日設為昨天、今天、明天與五天後。
2. 確認清單依序顯示「已過期」、「今天到期」、「3 天內到期」、「正常」，並按這個風險順序排列。
3. 確認摘要的「目前食材」為 `4`，「即將到期」為 `3`（已過期、今天到期與三天內到期皆需處理）。
4. 重新整理瀏覽器，確認四筆食材及其狀態仍在。
5. 點擊任一食材右側的 `×`，確認它從清單和摘要移除。
6. 確認「今晚吃什麼」使用優先食材產生建議。點擊加入缺少材料後，確認採買清單出現材料，重新整理後仍保留。
7. 點擊「複製 LINE 草稿」，確認高風險食材被整理為可貼到 LINE 的文字。此功能需要透過 `http://localhost:4173` 開啟，不能用直接開檔模式測試剪貼簿。

## 資料形狀

```js
{
  id: "UUID",
  name: "牛奶",
  quantity: 1,
  unit: "瓶",
  location: "fridge",
  expiresAt: "2026-11-08",
  createdAt: "2026-11-07T00:00:00.000Z"
}
```
