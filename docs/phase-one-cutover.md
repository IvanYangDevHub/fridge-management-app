# 第一階段 SQLite 到 PostgreSQL 切換

此程序在 DigitalOcean Droplet 上使用同一個 Compose 專案切換服務。先在測試環境演練成功切換及核對失敗後的復原。操作需 Python 3.11 以上、Docker Compose、舊版目錄、此版本目錄，以及已設定 TLS 的受管 PostgreSQL。

1. 保留舊版程式、原本的 `.env` 和資料掛載。新版放在另一個目錄，建立新版 `.env`，確認 PostgreSQL 連線使用 `SSL Mode=VerifyFull` 與可驗證的根憑證。先在新版目錄執行 `docker compose config --quiet` 及 `docker compose build`。
2. 從新版目錄執行下列 `verify`。它會停止舊版網站與後端，建立 SQLite 一致性備份和 SHA-256，再用唯讀備份執行試搬與正式搬遷。證據目錄須限制存取。記錄停寫時間及備份時間，並將備份複製到 Droplet 以外的安全位置。

   ```bash
   python3 scripts/phase-one-cutover.py verify \
     --project freshdays --legacy-dir /opt/freshdays-legacy \
     --postgres-dir /opt/freshdays-postgres --state /var/lib/freshdays/cutover-001
   ```

3. 檢查證據目錄中 `dry-run.json` 與 `transfer.json` 的庫存、採買、訂閱及通知四組筆數與內容。兩份報告均成功且確認 PostgreSQL 目標正確後，以同一組參數將 `verify` 換成 `open`。若核對失敗，保持停寫並修正資料後用相同備份重跑，或在執行 `open` 前改用 `restore` 恢復原 SQLite 服務。
4. 開放後檢查 HTTPS `/health`、原配對碼登入、庫存與採買讀寫，以及 LINE webhook、訂閱與到期提醒。記錄開放時間與結果。從首次執行 `open` 起，後端可能已在 PostgreSQL 寫入，故障時停止服務並修復或還原 PostgreSQL。

## 演練證據

本機以隔離 Compose 專案、SQLite 測試資料與 PostgreSQL 17 執行。成功流程中，四組各一筆均通過核對，停寫期間 HTTP 無法存取，恢復後庫存、採買和簽署的 LINE webhook 通過。這次 fixture 切換約 8.46 秒。失敗流程以預先寫入的 PostgreSQL 資料觸發核對失敗，`open` 拒絕開放，SQLite 從快照復原後通過 HTTP 與簽署 webhook，約 5.90 秒。

正式上線前，在受管資料庫執行時間點還原到獨立叢集，核對四組資料與近期寫入。記錄故障模擬時間、可復原的最後一筆寫入時間、服務重新通過冒煙測試的時間。可復原資料落差須在 1 小時內，服務須在數小時內恢復。正式環境的時間點還原、實際 LINE 推送與切換維護時段仍待驗收。
