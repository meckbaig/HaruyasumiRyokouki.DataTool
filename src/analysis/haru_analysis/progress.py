"""Single-line console progress: 'embed 120/4200'."""

import sys


class Progress:
    def __init__(self, name, total, stream=sys.stderr):
        self.name = name
        self.total = total
        self.done = 0
        self.stream = stream

    def step(self):
        self.done += 1
        self.stream.write(f"\r{self.name} {self.done}/{self.total}")
        self.stream.flush()

    def close(self):
        if self.done:
            self.stream.write("\n")
            self.stream.flush()
