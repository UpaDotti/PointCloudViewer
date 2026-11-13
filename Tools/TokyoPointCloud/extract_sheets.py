"""ZIPフォルダの中のZIPをすべて展開し、LASを1つのフォルダへ並べる（CRC検査つき）。

展開済みで大きさが合うファイルは飛ばす。展開したフォルダを PointCloudConverter の入力にする。

使い方:
    python extract_sheets.py <ZIPのフォルダ> <LASの出力フォルダ>
"""
import concurrent.futures as cf
import os
import sys
import zipfile


def extract(src, dst, name):
    out = []
    with zipfile.ZipFile(os.path.join(src, name)) as z:
        for info in z.infolist():
            if info.is_dir():
                continue
            fp = os.path.join(dst, os.path.basename(info.filename))
            if os.path.exists(fp) and os.path.getsize(fp) == info.file_size:
                out.append(info.file_size)
                continue
            with z.open(info) as s, open(fp + ".part", "wb") as d:  # z.open は読み終わりでCRCを検査する
                while chunk := s.read(1 << 22):
                    d.write(chunk)
            os.replace(fp + ".part", fp)
            out.append(info.file_size)
    return name, out


def main():
    src, dst = sys.argv[1], sys.argv[2]
    os.makedirs(dst, exist_ok=True)
    zips = sorted(f for f in os.listdir(src) if f.endswith(".zip"))
    files = 0
    total = 0
    empty = []
    with cf.ThreadPoolExecutor(4) as ex:
        for i, (name, sizes) in enumerate(ex.map(lambda n: extract(src, dst, n), zips), 1):
            if not sizes:
                empty.append(name)
            files += len(sizes)
            total += sum(sizes)
            if i % 50 == 0 or i == len(zips):
                print(f"{i}/{len(zips)} ZIP  {files}ファイル  {total / 1e9:.1f}GB", flush=True)
    print("中身の無いZIP:", empty if empty else "なし", flush=True)
    print("拡張子:", sorted({os.path.splitext(f)[1] for f in os.listdir(dst)}), flush=True)


if __name__ == "__main__":
    main()
