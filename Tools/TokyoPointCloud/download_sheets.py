"""select_sheets.py が書いた sheets.csv のZIPをすべてダウンロードする。

再実行すると、大きさが合う済みのファイルは飛ばし、途中のファイル（.part）は続きから落とす。
最後に、すべてのファイルの大きさが sheets.csv の zip_bytes と合うかを確かめる。

使い方:
    python download_sheets.py <sheets.csv> <ZIPの保存先フォルダ>
"""
import concurrent.futures as cf
import csv
import os
import sys
import threading
import time
import urllib.request


def main():
    csv_path, dest = sys.argv[1], sys.argv[2]
    os.makedirs(dest, exist_ok=True)
    with open(csv_path, encoding="utf-8-sig") as f:
        rows = list(csv.DictReader(f))
    total = sum(int(r["zip_bytes"]) for r in rows)
    lock = threading.Lock()
    progress = {"bytes": 0, "count": 0}
    failed = []
    t0 = time.time()

    def local_path(row):
        return os.path.join(dest, row["URL"].rsplit("/", 1)[1])

    def fetch(row):
        url, size = row["URL"], int(row["zip_bytes"])
        fp = local_path(row)
        part = fp + ".part"
        error = None
        for attempt in range(5):
            try:
                if os.path.exists(fp) and os.path.getsize(fp) == size:
                    break
                have = os.path.getsize(part) if os.path.exists(part) else 0
                req = urllib.request.Request(url, headers={"Range": f"bytes={have}-"} if have else {})
                with urllib.request.urlopen(req, timeout=60) as resp:
                    with open(part, "ab" if have and resp.status == 206 else "wb") as out:
                        while chunk := resp.read(1 << 20):
                            out.write(chunk)
                if os.path.getsize(part) != size:
                    raise IOError(f"大きさ {os.path.getsize(part)} != {size}")
                os.replace(part, fp)
                break
            except Exception as e:  # 通信の失敗は待ってからやり直す
                error = e
                time.sleep(5 * (attempt + 1))
        else:
            with lock:
                failed.append((row["MESH_NO"], str(error)))
            return
        with lock:
            progress["bytes"] += size
            progress["count"] += 1
            if progress["count"] % 10 == 0 or progress["count"] == len(rows):
                minutes = (time.time() - t0) / 60
                print(f"{progress['count']}/{len(rows)}  {progress['bytes'] / 1e9:.1f}/{total / 1e9:.1f}GB  {minutes:.0f}min", flush=True)

    with cf.ThreadPoolExecutor(6) as ex:
        list(ex.map(fetch, rows))

    print("失敗:", failed if failed else "なし", flush=True)
    ok = sum(1 for r in rows if os.path.exists(local_path(r)) and os.path.getsize(local_path(r)) == int(r["zip_bytes"]))
    print(f"大きさが合うファイル: {ok}/{len(rows)}", flush=True)


if __name__ == "__main__":
    main()
