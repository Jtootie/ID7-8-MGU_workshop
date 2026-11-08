"""Use UTF-8 when the Windows launcher redirects the helper's logs."""
import sys

for stream in (sys.stdout, sys.stderr):
    if stream is not None:
        stream.reconfigure(encoding="utf-8", errors="backslashreplace")
