#!/usr/bin/env python3
"""Small stdlib GUI for running 3commerce shell scripts and inspecting local status.

Run from the repo root:
    python3 tools/script-console/script_console.py

No third-party packages are required. The GUI discovers scripts/*.sh dynamically and
runs each in its own subprocess, streaming output into the log panel.
"""
from __future__ import annotations

import os
import platform
import queue
import shlex
import shutil
import subprocess
import sys
import threading
import time
import tkinter as tk
from pathlib import Path
from tkinter import ttk, messagebox

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS_DIR = ROOT / "scripts"


def run_text(command: list[str], timeout: int = 8) -> str:
    try:
        completed = subprocess.run(
            command,
            cwd=ROOT,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            timeout=timeout,
            check=False,
        )
        return completed.stdout.strip() or f"(exit {completed.returncode}, no output)"
    except FileNotFoundError:
        return f"not installed: {command[0]}"
    except subprocess.TimeoutExpired:
        return f"timeout after {timeout}s: {' '.join(command)}"


class ScriptConsole(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title("3commerce Script Console")
        self.geometry("1220x820")
        self.queue: queue.Queue[tuple[str, str]] = queue.Queue()
        self.running: dict[str, subprocess.Popen[str]] = {}

        self.args_var = tk.StringVar()
        self.status_var = tk.StringVar(value="Ready")

        self._build_ui()
        self._poll_queue()
        self.refresh_status()

    def _build_ui(self) -> None:
        root = ttk.PanedWindow(self, orient=tk.HORIZONTAL)
        root.pack(fill=tk.BOTH, expand=True, padx=8, pady=8)

        left = ttk.Frame(root, padding=8)
        right = ttk.Frame(root, padding=8)
        root.add(left, weight=1)
        root.add(right, weight=2)

        ttk.Label(left, text="scripts/*.sh", font=("TkDefaultFont", 14, "bold")).pack(anchor=tk.W)
        ttk.Label(left, text="Optional args for the next run:").pack(anchor=tk.W, pady=(8, 0))
        ttk.Entry(left, textvariable=self.args_var).pack(fill=tk.X, pady=(0, 8))

        canvas = tk.Canvas(left, highlightthickness=0)
        scrollbar = ttk.Scrollbar(left, orient=tk.VERTICAL, command=canvas.yview)
        buttons = ttk.Frame(canvas)
        buttons.bind("<Configure>", lambda event: canvas.configure(scrollregion=canvas.bbox("all")))
        canvas.create_window((0, 0), window=buttons, anchor="nw")
        canvas.configure(yscrollcommand=scrollbar.set)
        canvas.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        scrollbar.pack(side=tk.RIGHT, fill=tk.Y)

        for script in sorted(SCRIPTS_DIR.glob("*.sh")):
            row = ttk.Frame(buttons)
            row.pack(fill=tk.X, pady=2)
            ttk.Button(row, text=f"Run {script.name}", command=lambda s=script: self.run_script(s)).pack(side=tk.LEFT, fill=tk.X, expand=True)
            ttk.Button(row, text="Stop", command=lambda s=script: self.stop_script(s.name)).pack(side=tk.RIGHT, padx=(6, 0))

        top = ttk.Frame(right)
        top.pack(fill=tk.X)
        ttk.Button(top, text="Refresh status", command=self.refresh_status).pack(side=tk.LEFT)
        ttk.Button(top, text="Clear log", command=lambda: self.log.delete("1.0", tk.END)).pack(side=tk.LEFT, padx=6)
        ttk.Label(top, textvariable=self.status_var).pack(side=tk.RIGHT)

        notebook = ttk.Notebook(right)
        notebook.pack(fill=tk.BOTH, expand=True, pady=(8, 0))

        status_frame = ttk.Frame(notebook)
        log_frame = ttk.Frame(notebook)
        notebook.add(status_frame, text="Status")
        notebook.add(log_frame, text="Output")

        self.status = tk.Text(status_frame, wrap=tk.WORD, height=20)
        self.status.pack(fill=tk.BOTH, expand=True)
        self.log = tk.Text(log_frame, wrap=tk.WORD, height=20)
        self.log.pack(fill=tk.BOTH, expand=True)

    def run_script(self, script: Path) -> None:
        if script.name in self.running:
            messagebox.showinfo("Already running", f"{script.name} is already running.")
            return

        args = shlex.split(self.args_var.get().strip()) if self.args_var.get().strip() else []
        command = [str(script), *args]
        self._log("system", f"$ {' '.join(command)}\n")
        try:
            proc = subprocess.Popen(
                command,
                cwd=ROOT,
                text=True,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                bufsize=1,
            )
        except Exception as exc:  # noqa: BLE001 - displayed to operator
            self._log(script.name, f"failed to start: {exc}\n")
            return

        self.running[script.name] = proc
        self.status_var.set(f"Running {script.name}")
        threading.Thread(target=self._stream_process, args=(script.name, proc), daemon=True).start()

    def stop_script(self, name: str) -> None:
        proc = self.running.get(name)
        if not proc:
            return
        proc.terminate()
        self._log("system", f"sent terminate to {name}\n")

    def _stream_process(self, name: str, proc: subprocess.Popen[str]) -> None:
        assert proc.stdout is not None
        for line in proc.stdout:
            self.queue.put((name, line))
        code = proc.wait()
        self.queue.put(("system", f"{name} exited {code}\n"))
        self.running.pop(name, None)
        self.queue.put(("__status__", "Ready" if not self.running else f"Running: {', '.join(self.running)}"))

    def _poll_queue(self) -> None:
        try:
            while True:
                name, text = self.queue.get_nowait()
                if name == "__status__":
                    self.status_var.set(text)
                else:
                    self._log(name, text)
        except queue.Empty:
            pass
        self.after(100, self._poll_queue)

    def _log(self, source: str, text: str) -> None:
        timestamp = time.strftime("%H:%M:%S")
        self.log.insert(tk.END, f"[{timestamp} {source}] {text}")
        self.log.see(tk.END)

    def refresh_status(self) -> None:
        sections: list[tuple[str, str]] = []
        sections.append(("Host", self.host_status()))
        sections.append(("Tool versions", self.version_status()))
        sections.append(("Docker containers", run_text(["docker", "ps", "--format", "table {{.Names}}\t{{.Image}}\t{{.Status}}\t{{.Ports}}"])))
        sections.append(("3commerce images", run_text(["docker", "images", "--format", "table {{.Repository}}\t{{.Tag}}\t{{.ID}}\t{{.CreatedSince}}\t{{.Size}}", "3commerce/*"])))
        sections.append(("Compose services", run_text(["docker", "compose", "ps"])))
        sections.append(("Service health", self.service_health()))
        self.status.delete("1.0", tk.END)
        for title, body in sections:
            self.status.insert(tk.END, f"## {title}\n{body}\n\n")
        self.status_var.set("Status refreshed")

    def host_status(self) -> str:
        cpu = os.cpu_count() or "unknown"
        uname = platform.platform()
        disk = shutil.disk_usage(ROOT)
        mem = ""
        if sys.platform == "darwin":
            mem = run_text(["vm_stat"], timeout=3).splitlines()[0:8]
            mem = "\n".join(mem)
        else:
            mem = run_text(["free", "-h"], timeout=3)
        return f"platform: {uname}\npython: {platform.python_version()}\ncpu count: {cpu}\nrepo disk free: {disk.free // (1024**3)} GiB / {disk.total // (1024**3)} GiB\n{mem}"

    def version_status(self) -> str:
        commands = [
            ["dotnet", "--version"],
            ["node", "--version"],
            ["npm", "--version"],
            ["docker", "--version"],
            ["docker", "compose", "version"],
            ["helm", "version", "--short"],
            ["kubectl", "version", "--client", "--short"],
            ["gh", "--version"],
        ]
        lines = []
        for command in commands:
            lines.append(f"$ {' '.join(command)}\n{run_text(command, timeout=5)}")
        return "\n".join(lines)

    def service_health(self) -> str:
        lines = []
        services_file = ROOT / "scripts" / "lib" / "services.sh"
        for raw in services_file.read_text(encoding="utf-8").splitlines():
            raw = raw.strip().strip('"')
            if not raw or raw.startswith("#") or ":src/" not in raw:
                continue
            parts = raw.split(":")
            if len(parts) != 3 or not parts[2]:
                continue
            name, _, port = parts
            if name in {"gateway", "notifications"}:
                continue
            lines.append(f"{name:14} {run_text(['curl', '-s', '-o', '/dev/null', '-w', '%{http_code}', f'http://localhost:{port}/health/ready'], timeout=3)}")
        lines.append(f"{'gateway':14} {run_text(['curl', '-s', '-o', '/dev/null', '-w', '%{http_code}', 'http://localhost:8080/health'], timeout=3)}")
        return "\n".join(lines)


if __name__ == "__main__":
    app = ScriptConsole()
    app.mainloop()
