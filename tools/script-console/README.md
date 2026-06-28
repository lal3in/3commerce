# 3commerce Script Console

A small Python/Tkinter operator GUI for local development. It discovers every
`scripts/*.sh` file, shows the scripts in a normal execution order, explains in
plain language what each one does, gives each script Run/Stop buttons, and streams
output. Each script row also has **View/Edit .sh**, which opens the exact shell
script in the built-in editor so you can visually inspect or update what the button
will trigger before running it.

The status dashboard shows:

- Docker containers and `3commerce/*` images
- `docker compose ps`
- service readiness endpoints from `scripts/lib/services.sh`
- local tool versions (`dotnet`, Node/npm, Docker/Compose, Helm, kubectl, gh)
- basic host stats (CPU, disk, memory snapshot)

Run it from the repository root:

```bash
python3 tools/script-console/script_console.py
```

No third-party Python packages are required. On macOS, the system Python normally
includes Tkinter; if it does not, install a Python distribution that includes Tk.

The left panel is ordered by purpose: check first → start dev → load demo data →
service-only control → build/launch → regression → diagnostics/CI/secrets → stop.

Use the optional-args field before clicking a script button, e.g.:

- `--with-frontends --seed` for `dev-up.sh`
- `--profile full` for `dev-dummy-data.sh`
- `--deep --logs` for `host-check.sh`
