"""Command-line entry point for the photo analysis pipeline."""

import argparse
import json

from . import db, index, metadata, ocr, vlm, embed, report, translate


def main(argv=None):
    parser = argparse.ArgumentParser(prog="photolib")
    parser.add_argument("--db", required=True)
    sub = parser.add_subparsers(dest="cmd", required=True)
    sub.add_parser("init")
    p_index = sub.add_parser("index")
    p_index.add_argument("--root", required=True)
    sub.add_parser("metadata")
    sub.add_parser("ocr")
    p_vlm = sub.add_parser("vlm")
    p_vlm.add_argument("--model", default=vlm.DEFAULT_MODEL)
    p_vlm.add_argument("--num-ctx", type=int, default=vlm.DEFAULT_NUM_CTX)
    p_vlm.add_argument("--max-side", type=int, default=vlm.MAX_IMAGE_SIDE)
    p_embed = sub.add_parser("embed")
    p_embed.add_argument("--model-id", default=embed.DEFAULT_MODEL_ID)
    p_translate = sub.add_parser("translate")
    p_translate.add_argument("--lang", required=True, choices=["ru", "ja"])
    p_translate.add_argument("--model", default=translate.DEFAULT_MODEL)
    sub.add_parser("report")

    args = parser.parse_args(argv)
    conn = db.connect(args.db)

    if args.cmd == "init":
        db.init_db(conn)
    elif args.cmd == "index":
        db.init_db(conn)
        print(index.index_images(conn, args.root))
    elif args.cmd == "metadata":
        metadata.run_metadata(conn)
    elif args.cmd == "ocr":
        ocr.run_ocr(conn)
    elif args.cmd == "vlm":
        vlm.run_vlm(conn, args.model, args.num_ctx, args.max_side)
    elif args.cmd == "embed":
        embed.run_embed(conn, args.model_id)
    elif args.cmd == "translate":
        translate.run_translate(conn, args.lang, args.model)
    elif args.cmd == "report":
        print(json.dumps(report.summary(conn), indent=2))
        errs = report.errors(conn)
        if errs:
            print("\nErrors:")
            for row in errs:
                for stage in db.STAGES:
                    if row["status_" + stage] == "error":
                        print("  " + str(row["id"]) + " " + row["path"]
                              + " [" + stage + "] " + str(row["error_" + stage]))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
