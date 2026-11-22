# 第二階段多家庭切換與驗收

第二階段以 `Product__PhaseTwo=true` 啟用 Google 登入、家庭清單逐筆操作、成員權限及 LINE 家庭綁定。請先完成第一階段的搬遷與核對，再在有獨立 PostgreSQL 及 LINE 測試官方帳號的 HTTPS 環境演練。若第一階段仍在使用，共用配對碼只在第一階段有效。[第一階段切換與復原紀錄](phase-one-cutover.md)

## 自動化驗收

以 `Phase two release` workflow 驗收預定發布的 commit。GitHub Actions 使用 PostgreSQL 17、.NET 10 與 Node 22，並保存 `phase-two-release-evidence`。使用測試用 PostgreSQL 時，資料庫角色須能建立和移除隔離 schema。以下指令可在 repo 根目錄重跑同一套驗收，`FRESHDAYS_TEST_POSTGRES` 應指向可拋棄的 PostgreSQL。

```bash
results=$(mktemp -d)
dotnet test backend/tests/FreshDays.Tests --collect 'XPlat Code Coverage' \
  --results-directory "$results" --logger 'trx;LogFileName=backend.trx' \
  --logger 'console;verbosity=detailed' -- \
  DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=json
npm --prefix frontend ci
npm --prefix frontend run test:coverage -- --coverage.reporter=lcov
npm --prefix frontend run build
python -m unittest discover -s scripts -p test_release_coverage.py
report=$(find "$results" -name coverage.json -print -quit)
python scripts/release_coverage.py --base origin/main --coverlet "$report" \
  --lcov frontend/coverage/lcov.info
```

工作負載測試以 HTTP 建立 100 個家庭並讓 500 個已登入帳號透過邀請加入。每一波同時放行 50 個成員操作，兩種清單共寫入 1,000 筆版本化更新。每個帳號重新讀取兩種清單，核對項目 ID、內容與版本。跨家庭讀寫必須返回 `403`。五人同改一筆時，僅一筆可提交，其餘四筆必須回報 `409`，重新讀取後應取得成功提交的內容。後端負載測試測量開啟中的 HTTP 工作階段約 15 秒後的重新讀取，Vitest 另以 React 畫面驗證兩種清單在 15 秒輪詢時顯示變更。

覆蓋率檢查以 Git 差異判定新增和修改的可執行行及分支起點。Coverlet JSON 提供 C# 個別分支結果，Vitest V8 LCOV 提供瀏覽器程式結果。這些範圍內的行與分支須全數通過，任一報告缺漏或變更來源缺少量測時停止發布。搬遷工具的 `TransferSql.cs` 只有編譯期常數，內含 SQL 由真實 PostgreSQL 搬遷測試執行。第一階段 Python 工具另有 `Phase one cutover` workflow。

## 預備環境驗收與上線

1. 使用相同映像在 HTTPS 預備環境演練。準備 Google OAuth 網站來源、LINE Messaging API 測試頻道、PostgreSQL TLS 憑證及專用資料庫帳號。Google 的網站來源需對應演練網域。以 PostgreSQL 的既有家庭資料副本演練認領，並設定 `Auth__LegacyOwnerEmail` 為有權認領的已驗證 Google Email。
2. 在該環境的 `.env` 設定 `Product__PhaseTwo=true`、`Auth__GoogleClientId`、`Auth__LegacyOwnerEmail`、`ConnectionStrings__FreshDays` 和 LINE 憑證。連線使用 `SSL Mode=VerifyFull`。移除舊的 `HOUSEHOLD_CODE` 設定。
3. 以 `docker compose config --quiet` 檢查設定。可用下列指令確認後端收到第二階段開關。建置 web 與 backend 映像，再於維護時段以 `docker compose stop web backend` 停寫。確認有獨立可用的 PostgreSQL 還原點後，以 `docker compose up -d --force-recreate` 啟動兩個服務。

   ```bash
   docker compose config --format json |
     jq -e '.services.backend.environment.Product__PhaseTwo == "true"'
   ```

4. 檢查 `/health` 及 `/api/auth/config` 的 `phase-two` 設定。兩個舊入口 `/api/inventory`、`/api/shopping` 的 `GET`、`PUT`、`POST` 均須回傳 `404`。線上後端只使用 PostgreSQL。SQLite 讀取程式屬第一階段手動搬遷工具。
5. 用兩個 Google 帳號驗證登入、合格帳號認領舊家庭、邀請加入、成員移除及跨家庭拒絕。兩個瀏覽器同時開啟同家庭，分別寫入食材與採買項目，記錄提交至另一畫面顯示的時間。再針對相同版本寫入同一筆，確認只接受一次且畫面顯示衝突並重新載入。讓成員刪除測試帳號並確認工作階段與署名移除，共用項目保留。再由擁有者確認刪除演練家庭，驗證家庭與清單均無法存取。
6. 驗證 LINE webhook，確認未重新綁定的舊訂閱者收到重新綁定提示。由已登入成員取得綁定指令並完成綁定，從測試官方帳號查詢到期食材並驗證單次實際推送。移除成員後重試查詢及推送，確認存取權撤銷。實際 LINE 推送、Google 登入及下述受管備份演練都需保留驗收紀錄。
7. 預備環境、相同 commit 的 CI 和受管 PostgreSQL 還原均完成後，依同樣設定安排正式切換。發布受阻時保持停寫。第二階段一旦接受多家庭寫入，保留第二階段模式，使用 PostgreSQL 修復或還原。

## DigitalOcean 時間點還原演練

在受管 PostgreSQL 演練資料上寫入可核對的近期資料，記下每次已提交寫入的 UTC 時間。記錄故障模擬及開始停寫的時間。在 DigitalOcean 將叢集還原到指定時間點，選擇獨立的新叢集，再用隔離的應用程式連線核對資料。核對家庭及成員、庫存、採買、LINE 綁定與通知紀錄，確認要求刪除的線上資料在所選還原時間點的狀態。通過 HTTPS、登入與主要讀寫操演後，記錄服務可用時間。

- RPO 以故障模擬時間減去成功復原的最後一筆提交時間計算，須在 1 小時內。
- RTO 以停止服務至還原環境重新通過冒煙測試的時間計算，以小時計，須在數小時內。
- 記錄 commit、CI artifact、演練叢集、還原時間點、三個 UTC 時間、量測結果、冒煙結果與驗收人。正式發布前應確認兩個門檻均達標。[DigitalOcean PostgreSQL 還原指南](https://docs.digitalocean.com/products/databases/postgresql/how-to/restore-from-backups/)

## 演練紀錄

2026-11-23 在本機以隔離 PostgreSQL 17 執行，後端 84 項測試及前端 53 項測試全數通過，正式版前端建置成功。相對 `66bd32fe` 的 33 個變更來源達到 100% 可執行行與分支覆蓋率。第一階段 Python 工具的 12 項測試及第二階段覆蓋率工具的 5 項測試全數通過。第二階段 Compose 模型成功將產品開關設為 `true`。

完整覆蓋率測試當次建立 100 個家庭與 500 位成員花費 2.58 秒，50 人一波完成 1,000 次更新及跨家庭讀寫拒絕檢查共花費 1.98 秒。開啟中的 HTTP 工作階段於 15.03 秒讀到該家庭的兩份最新清單，全部 500 位成員均核對成功。同筆衝突與 React 的 15 秒畫面更新測試也通過。

第一階段本機 Compose fixture 的成功切換約 8.46 秒。核對失敗後阻擋開放，再從快照恢復 SQLite 並通過 HTTP 與簽署 webhook，約 5.90 秒。正式受管 PostgreSQL 時間點還原、實際 Google 登入、LINE 推送和正式切換維護時段待驗收。[第一階段本機演練](phase-one-cutover.md)
