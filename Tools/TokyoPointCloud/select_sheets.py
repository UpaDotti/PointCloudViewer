"""東京都区部点群データ（1/500図郭）のうち、山手線の内側に重なる図郭を選ぶ。

配布ページのプレビューが使うベクトルタイル（図郭番号 MESH_NO とZIPのURLを持つ）を読み、
山手線30駅を結んだ多角形と重なる図郭を選んで、周囲8方向へ --expand 枚分広げる。
ZIPの大きさはHEADリクエストで取り、ダウンロードはしない。

使い方:
    pip install -r requirements.txt
    python select_sheets.py <出力フォルダ> [--expand 1]

出力:
    sheets.csv      MESH_NO, inside_ratio（山手線の内側にある面積の割合）, expand_ring（0=山手線にかかる, 1以上=広げて足した）, zip_bytes, URL
    sheets.geojson  図郭の矩形と山手線の多角形（WGS84）
"""
import argparse
import concurrent.futures as cf
import csv
import json
import math
import os
import urllib.error
import urllib.request
from collections import defaultdict

import mapbox_vector_tile
from pyproj import Transformer
from shapely.geometry import Polygon, box, mapping
from shapely.ops import transform

TILE_URL = "https://gic-tokyo.s3.ap-northeast-1.amazonaws.com/2024/dig/Vectortile2026/23ku/lp/{z}/{x}/{y}.pbf"
TILE_ZOOM = 15  # この段から下はタイル作成時の図形の間引きがない
SHEET_W, SHEET_H = 400, 300  # 1/500図郭の大きさ（m、平面直角座標）

# 山手線30駅（WGS84の概略値、外回りの順）。駅の間は直線で結ぶので、線路の曲がりは表さない
STATIONS = [
    ("東京", 35.6812, 139.7671), ("神田", 35.6918, 139.7709), ("秋葉原", 35.6984, 139.7731),
    ("御徒町", 35.7074, 139.7746), ("上野", 35.7138, 139.7773), ("鶯谷", 35.7206, 139.7781),
    ("日暮里", 35.7281, 139.7710), ("西日暮里", 35.7320, 139.7668), ("田端", 35.7381, 139.7608),
    ("駒込", 35.7365, 139.7470), ("巣鴨", 35.7334, 139.7393), ("大塚", 35.7318, 139.7286),
    ("池袋", 35.7295, 139.7109), ("目白", 35.7212, 139.7065), ("高田馬場", 35.7125, 139.7038),
    ("新大久保", 35.7013, 139.7000), ("新宿", 35.6896, 139.7006), ("代々木", 35.6830, 139.7020),
    ("原宿", 35.6702, 139.7027), ("渋谷", 35.6580, 139.7016), ("恵比寿", 35.6467, 139.7101),
    ("目黒", 35.6339, 139.7157), ("五反田", 35.6262, 139.7236), ("大崎", 35.6197, 139.7286),
    ("品川", 35.6285, 139.7388), ("高輪ゲートウェイ", 35.6355, 139.7407), ("田町", 35.6457, 139.7476),
    ("浜松町", 35.6555, 139.7570), ("新橋", 35.6662, 139.7583), ("有楽町", 35.6751, 139.7630),
]

TO_PLANE = Transformer.from_crs("EPSG:4326", "EPSG:6677", always_xy=True)  # JGD2011 平面直角座標系 第IX系
TO_LONLAT = Transformer.from_crs("EPSG:6677", "EPSG:4326", always_xy=True)


def lonlat_to_tile(lon, lat, z):
    n = 2 ** z
    x = int((lon + 180) / 360 * n)
    y = int((1 - math.asinh(math.tan(math.radians(lat))) / math.pi) / 2 * n)
    return x, y


def tile_px_to_lonlat(tx, ty, z, px, py, extent):
    n = 2 ** z
    lon = (tx + px / extent) / n * 360 - 180
    lat = math.degrees(math.atan(math.sinh(math.pi * (1 - 2 * (ty + py / extent) / n))))
    return lon, lat


def fetch_tile(cache_dir, tx, ty):
    """タイルを取得する（キャッシュがあれば使う）。範囲外のタイルは空を返す"""
    fp = os.path.join(cache_dir, f"{TILE_ZOOM}_{tx}_{ty}.pbf")
    if not os.path.exists(fp):
        try:
            data = urllib.request.urlopen(TILE_URL.format(z=TILE_ZOOM, x=tx, y=ty), timeout=30).read()
        except urllib.error.HTTPError as e:
            if e.code not in (403, 404):
                raise
            data = b""
        with open(fp, "wb") as f:
            f.write(data)
    with open(fp, "rb") as f:
        return f.read()


def read_sheets(cache_dir, lons, lats, pad_deg):
    """駅の範囲＋余白を覆うタイルを読み、図郭ごとに (URL, 平面直角座標の矩形) を返す"""
    x0, y0 = lonlat_to_tile(min(lons) - pad_deg, max(lats) + pad_deg, TILE_ZOOM)
    x1, y1 = lonlat_to_tile(max(lons) + pad_deg, min(lats) - pad_deg, TILE_ZOOM)
    points, urls = defaultdict(list), {}
    for tx in range(x0, x1 + 1):
        for ty in range(y0, y1 + 1):
            data = fetch_tile(cache_dir, tx, ty)
            if not data:
                continue
            for layer in mapbox_vector_tile.decode(data, default_options={"y_coord_down": True}).values():
                extent = layer.get("extent", 4096)
                for feature in layer["features"]:
                    mesh = feature["properties"]["MESH_NO"]
                    urls[mesh] = feature["properties"]["URL"]
                    geom = feature["geometry"]
                    polys = geom["coordinates"] if geom["type"] == "MultiPolygon" else [geom["coordinates"]]
                    for poly in polys:
                        for px, py in poly[0]:
                            points[mesh].append(TO_PLANE.transform(*tile_px_to_lonlat(tx, ty, TILE_ZOOM, px, py, extent)))

    # タイルで切られた図郭の断片を合わせ、100m単位に丸めて元の矩形に戻す
    # 読んだ範囲の外周で切れた図郭は矩形が欠けるので除く（駅から余白分離れているため、選ぶ範囲には入らない）
    sheets, clipped = {}, 0
    for mesh, ps in points.items():
        xs = [p[0] for p in ps]
        ys = [p[1] for p in ps]
        sx, sy = round(min(xs) / 100) * 100, round(min(ys) / 100) * 100
        ex, ey = round(max(xs) / 100) * 100, round(max(ys) / 100) * 100
        if (ex - sx, ey - sy) != (SHEET_W, SHEET_H):
            clipped += 1
            continue
        sheets[mesh] = (urls[mesh], box(sx, sy, ex, ey))
    print(f"読んだ図郭: {len(sheets)}枚（外周で欠けて除いた {clipped}枚）")
    return sheets


def head_size(url):
    with urllib.request.urlopen(urllib.request.Request(url, method="HEAD"), timeout=30) as resp:
        return int(resp.headers["Content-Length"])


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("out_dir")
    parser.add_argument("--expand", type=int, default=1, help="周囲8方向に何枚分広げるか（既定 1）")
    parser.add_argument("--skip-sizes", action="store_true", help="ZIPの大きさを取らない（zip_bytes は空になる）")
    args = parser.parse_args()

    cache_dir = os.path.join(args.out_dir, "tiles")
    os.makedirs(cache_dir, exist_ok=True)

    ring = Polygon([TO_PLANE.transform(lon, lat) for _, lat, lon in STATIONS])
    # 余白は約1.1km（0.01度）＋広げる枚数分。広げた先の図郭が外周の欠けに当たらないようにする
    pad_deg = 0.01 * (1 + args.expand)
    sheets = read_sheets(cache_dir, [s[2] for s in STATIONS], [s[1] for s in STATIONS], pad_deg)

    rows = {}
    for mesh, (url, rect) in sheets.items():
        ratio = rect.intersection(ring).area / rect.area
        rows[mesh] = dict(mesh=mesh, url=url, rect=rect, ratio=ratio, ring=0, origin=(rect.bounds[0], rect.bounds[1]))

    by_origin = {r["origin"]: r for r in rows.values()}
    selected = {r["origin"] for r in rows.values() if r["ratio"] > 0}
    for k in range(1, args.expand + 1):
        grown = {(x + dx * SHEET_W, y + dy * SHEET_H) for x, y in selected for dx in (-1, 0, 1) for dy in (-1, 0, 1)}
        for origin in grown - selected:
            if origin in by_origin:
                by_origin[origin]["ring"] = k
        selected |= grown
    missing = [o for o in selected if o not in by_origin]
    chosen = sorted((by_origin[o] for o in selected if o in by_origin), key=lambda r: r["mesh"])

    if args.skip_sizes:
        sizes = [""] * len(chosen)
    else:
        with cf.ThreadPoolExecutor(16) as ex:
            sizes = list(ex.map(head_size, [r["url"] for r in chosen]))

    with open(os.path.join(args.out_dir, "sheets.csv"), "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f, lineterminator="\n")
        w.writerow(["MESH_NO", "inside_ratio", "expand_ring", "zip_bytes", "URL"])
        for r, size in zip(chosen, sizes):
            w.writerow([r["mesh"], f"{r['ratio']:.3f}", r["ring"], size, r["url"]])

    to_lonlat = lambda g: transform(lambda x, y, z=None: TO_LONLAT.transform(x, y), g)
    features = [{"type": "Feature",
                 "properties": {"MESH_NO": r["mesh"], "inside_ratio": round(r["ratio"], 3), "expand_ring": r["ring"]},
                 "geometry": mapping(to_lonlat(r["rect"]))} for r in chosen]
    features.append({"type": "Feature", "properties": {"name": "yamanote_ring"}, "geometry": mapping(to_lonlat(ring))})
    with open(os.path.join(args.out_dir, "sheets.geojson"), "w", encoding="utf-8") as f:
        json.dump({"type": "FeatureCollection", "features": features}, f, ensure_ascii=False)

    inside = sum(r["ring"] == 0 for r in chosen)
    print(f"山手線の多角形: {ring.area / 1e6:.1f} km2")
    print(f"選んだ図郭: {len(chosen)}枚（山手線にかかる {inside}、広げて足した {len(chosen) - inside}、データの無いマス {len(missing)}）")
    if not args.skip_sizes:
        print(f"ZIPの合計: {sum(sizes) / 1e9:.1f} GB")


if __name__ == "__main__":
    main()
