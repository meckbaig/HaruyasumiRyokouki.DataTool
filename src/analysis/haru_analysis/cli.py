"""Command-line entry point: haru-analysis <step> --workspace <folder>."""

import argparse
import json
import sys

from . import db, embed, frames, scan, status, tag
from .workspace import EXIT_ERROR, EXIT_OK, StepReport, Workspace, WorkspaceError


def main(argv=None):
    parser = argparse.ArgumentParser(prog="haru-analysis")
    sub = parser.add_subparsers(dest="cmd", required=True)
    p_scan = sub.add_parser("scan", help="index photos and videos of the source folder")
    p_frames = sub.add_parser("frames", help="extract the frame at 1 s of every video (ffmpeg)")
    p_tag = sub.add_parser("tag", help="machine tags for every file from the vision model in [usage] vision")
    p_tag.add_argument("--vocabulary-hint", metavar="FILE",
                       help="text file with the site's tags, one per line; the model prefers them")
    p_tag.add_argument("--reset", action="store_true", help="drop all machine tags and ask the model again")
    p_embed = sub.add_parser("embed", help="compute SigLIP 2 image vectors")
    p_embed.add_argument("--reset", action="store_true", help="drop all vectors and recompute them")
    p_status = sub.add_parser("status", help="show per-step counts and errors")
    for p in (p_scan, p_frames, p_tag, p_embed, p_status):
        p.add_argument("--workspace", required=True, help="workspace folder with haru-data.toml")

    args = parser.parse_args(argv)
    try:
        workspace = Workspace(args.workspace)
        workspace.load_env()
        conn = workspace.connect()
    except (WorkspaceError, db.SchemaError) as exc:
        print("error: " + str(exc), file=sys.stderr)
        return EXIT_ERROR

    if args.cmd == "status":
        print(json.dumps(status.summary(conn), indent=2))
        errs = status.errors(conn)
        if errs:
            print("\nErrors:")
            for stage, key, path, error in errs:
                print(f"  [{stage}] {key} {path}: {error}")
        return EXIT_OK

    report = StepReport(args.cmd)
    try:
        if args.cmd == "scan":
            scan.run_scan(conn, workspace.source, report)
        elif args.cmd == "frames":
            frames.run_frames(conn, workspace, report)
        elif args.cmd == "tag":
            hint = tag.read_hint(args.vocabulary_hint) if args.vocabulary_hint else None
            tag.run_tag(conn, workspace, report, hint_tags=hint, reset=args.reset)
        elif args.cmd == "embed":
            embed.run_embed(conn, workspace, report, reset=args.reset)
    except KeyboardInterrupt:
        # Every file is committed as soon as it is done, so a rerun continues from here.
        print("\ninterrupted, run the step again to continue", file=sys.stderr)
        report.message = "interrupted"
        return report.finish(workspace, "error")
    except (WorkspaceError, frames.FfmpegNotFound, embed.ModelMismatch, tag.ServerUnavailable, OSError) as exc:
        report.message = str(exc)
        print("error: " + str(exc), file=sys.stderr)
        return report.finish(workspace, "error")

    print(f"{args.cmd}: " + ", ".join(f"{k} {v}" for k, v in report.counts.items()))
    if report.errors:
        print(f"{len(report.errors)} error(s), details in reports/{args.cmd}.json"
              " and 'haru-analysis status'", file=sys.stderr)
    if report.message:
        print(report.message, file=sys.stderr if report.errors else sys.stdout)
    return report.finish(workspace)


if __name__ == "__main__":
    raise SystemExit(main())
