#!/usr/bin/env python3
"""Small stdlib GUI for running 3commerce shell scripts and inspecting local status.

Run from the repo root:
    python3 tools/script-console/script_console.py

No third-party packages are required. The GUI lists scripts in a human workflow
order, explains what each script does, can run/stop them, and lets an operator
view or edit the .sh file before running it.
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
from dataclasses import dataclass
from pathlib import Path
from tkinter import ttk, messagebox

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS_DIR = ROOT / "scripts"


@dataclass(frozen=True)
class ScriptInfo:
    name: str
    purpose: str
    description: str


SCRIPT_ORDER: list[ScriptInfo] = [
    ScriptInfo("doctor.sh", "Check first", "Quick health check for local infra, services, gateway, and recent errors."),
    ScriptInfo("dev-up.sh", "Start dev", "Starts local Postgres/RabbitMQ, migrates DBs, runs services, and optionally starts frontends or data profiles."),
    ScriptInfo("dev-dummy-data.sh", "Load demo data", "Seeds a running dev stack through gateway APIs with catalog, demo users, and optional operator data."),
    ScriptInfo("run-all.sh", "Services only", "Starts or stops the gateway, DB-owning services, and workers as local dotnet processes."),
    ScriptInfo("build-images.sh", "Build containers", "Builds all container images with bounded concurrency to avoid Docker VM OOMs."),
    ScriptInfo("launch.sh", "Container launch", "Runs the full compose stack in dev/prod mode with fresh or reused data."),
    ScriptInfo("e2e-verify.sh", "Regression", "Runs the automated regression suite; --live also boots the stack and runs live flows."),
    ScriptInfo("host-check.sh", "Deep diagnostics", "Checks containers, service health, RabbitMQ, logs, resources, and optional remote hosts."),
    ScriptInfo("ci-logs.sh", "CI triage", "Fetches the latest GitHub Actions failures and extracts useful error lines."),
    ScriptInfo("rotate-secrets.sh", "Secrets", "Generates fresh internal-auth keys and seed-admin password values for prod-like launch."),
    ScriptInfo("dev-down.sh", "Stop dev", "Stops local services/frontends and tears down the bare-run dev environment."),
]


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
        self.geometry("1420x900")
        self.queue: queue.Queue[tuple[str, str]] = queue.Queue()
        self.running: dict[str, subprocess.Popen[str]] = {}
        self.loaded_script: Path | None = None

        self.args_var = tk.StringVar()
        self.status_var = tk.StringVar(value="Ready")
        self.editor_label_var = tk.StringVar(value="No script loaded")

        self._build_ui()
        self._poll_queue()
        self.refresh_status()

    def _script_infos(self) -> list[ScriptInfo]:
        known = {info.name: info for info in SCRIPT_ORDER}
        ordered = [info for info in SCRIPT_ORDER if (SCRIPTS_DIR / info.name).exists()]
        for script in sorted(SCRIPTS_DIR.glob("*.sh")):
            if script.name not in known:
                ordered.append(ScriptInfo(script.name, "Other", self._description_from_file(script)))
        return ordered

    def _description_from_file(self, script: Path) -> str:
        for line in script.read_text(encoding="utf-8", errors="replace").splitlines()[1:10]:
            stripped = line.strip()
            if stripped.startswith("#"):
                text = stripped.lstrip("#").strip()
                if text and not text.lower().startswith("usage:"):
                    return text
        return "Repository shell script. Open it in the editor to inspect the exact actions."

    def _build_ui(self) -> None:
        root = ttk.PanedWindow(self, orient=tk.HORIZONTAL)
        root.pack(fill=tk.BOTH, expand=True, padx=8, pady=8)

        left = ttk.Frame(root, padding=8)
        right = ttk.Frame(root, padding=8)
        root.add(left, weight=2)
        root.add(right, weight=3)

        ttk.Label(left, text="Script workflow", font=("TkDefaultFont", 14, "bold")).pack(anchor=tk.W)
        ttk.Label(left, text="Scripts are ordered by the usual human workflow: check → start → seed → build/launch → verify → diagnose → stop.", wraplength=470).pack(anchor=tk.W, pady=(2, 8))
        ttk.Label(left, text="Optional args for the next run:").pack(anchor=tk.W, pady=(4, 0))
        ttk.Entry(left, textvariable=self.args_var).pack(fill=tk.X, pady=(0, 8))

        canvas = tk.Canvas(left, highlightthickness=0)
        scrollbar = ttk.Scrollbar(left, orient=tk.VERTICAL, command=canvas.yview)
        buttons = ttk.Frame(canvas)
        buttons.bind("<Configure>", lambda event: canvas.configure(scrollregion=canvas.bbox("all")))
        canvas.create_window((0, 0), window=buttons, anchor="nw")
        canvas.configure(yscrollcommand=scrollbar.set)
        canvas.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        scrollbar.pack(side=tk.RIGHT, fill=tk.Y)

        for index, info in enumerate(self._script_infos(), start=1):
            self._add_script_row(buttons, index, info)

        top = ttk.Frame(right)
        top.pack(fill=tk.X)
        ttk.Button(top, text="Refresh status", command=self.refresh_status).pack(side=tk.LEFT)
        ttk.Button(top, text="Clear log", command=lambda: self.log.delete("1.0", tk.END)).pack(side=tk.LEFT, padx=6)
        ttk.Label(top, textvariable=self.status_var).pack(side=tk.RIGHT)

        notebook = ttk.Notebook(right)
        notebook.pack(fill=tk.BOTH, expand=True, pady=(8, 0))

        status_frame = ttk.Frame(notebook)
        log_frame = ttk.Frame(notebook)
        editor_frame = ttk.Frame(notebook)
        notebook.add(status_frame, text="Status")
        notebook.add(log_frame, text="Output")
        notebook.add(editor_frame, text="Script editor")

        self.status = tk.Text(status_frame, wrap=tk.WORD, height=20)
        self.status.pack(fill=tk.BOTH, expand=True)
        self.log = tk.Text(log_frame, wrap=tk.WORD, height=20)
        self.log.pack(fill=tk.BOTH, expand=True)

        editor_top = ttk.Frame(editor_frame)
        editor_top.pack(fill=tk.X)
        ttk.Label(editor_top, textvariable=self.editor_label_var).pack(side=tk.LEFT)
        ttk.Button(editor_top, text="Save script", command=self.save_loaded_script).pack(side=tk.RIGHT)
        ttk.Button(editor_top, text="Reload", command=self.reload_loaded_script).pack(side=tk.RIGHT, padx=6)
        self.editor = tk.Text(editor_frame, wrap=tk.NONE, undo=True)
        self.editor.pack(fill=tk.BOTH, expand=True, pady=(8, 0))

    def _add_script_row(self, parent: ttk.Frame, index: int, info: ScriptInfo) -> None:
        script = SCRIPTS_DIR / info.name
        frame = ttk.LabelFrame(parent, text=f"{index}. {info.purpose}: {info.name}", padding=8)
        frame.pack(fill=tk.X, pady=5)

        ttk.Label(frame, text=info.description, wraplength=450).pack(anchor=tk.W, fill=tk.X)
        row = ttk.Frame(frame)
        row.pack(fill=tk.X, pady=(6, 0))
        ttk.Button(row, text="Run", command=lambda s=script: self.run_script(s)).pack(side=tk.LEFT)
        ttk.Button(row, text="Stop", command=lambda s=script: self.stop_script(s.name)).pack(side=tk.LEFT, padx=6)
        ttk.Button(row, text="View/Edit .sh", command=lambda s=script: self.load_script(s)).pack(side=tk.LEFT)

    def load_script(self, script: Path) -> None:
        self.loaded_script = script
        self.editor_label_var.set(f"Editing {script.relative_to(ROOT)}")
        self.editor.delete("1.0", tk.END)
        self.editor.insert("1.0", script.read_text(encoding="utf-8", errors="replace"))
        self._log("system", f"loaded {script.relative_to(ROOT)} into editor\n")

    def reload_loaded_script(self) -> None:
        if self.loaded_script is None:
            messagebox.showinfo("No script loaded", "Click 'View/Edit .sh' next to a script first.")
            return
        self.load_script(self.loaded_script)

    def save_loaded_script(self) -> None:
        if self.loaded_script is None:
            messagebox.showinfo("No script loaded", "Click 'View/Edit .sh' next to a script first.")
            return
        if not messagebox.askyesno("Save script", f"Overwrite {self.loaded_script.relative_to(ROOT)}?"):
            return
        self.loaded_script.write_text(self.editor.get("1.0", tk.END).rstrip() + "\n", encoding="utf-8")
        self.loaded_script.chmod(self.loaded_script.stat().st_mode | 0o111)
        self._log("system", f"saved {self.loaded_script.relative_to(ROOT)}\n")

    def run_script(self, script: Path) -> None:
        if script.name in self.running:
            messagebox.showinfo("Already running", f"{script.name} is already running.")
            return

        args = shlex.split(self.args_var.get().strip()) if self.args_var.get().strip() else []
        command = [str(script), *args]
        self._log("system", f"$ {' '.join(shlex.quote(part) for part in command)}\n")
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
        if sys.platform == "darwin":
            mem_lines = run_text(["vm_stat"], timeout=3).splitlines()[0:8]
            mem = "\n".join(mem_lines)
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
