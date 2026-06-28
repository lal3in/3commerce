# 3commerce Script Console

A small Python/Tkinter operator GUI for local development. It discovers every
`scripts/*.sh` file, gives each script a Run button, streams output, and shows a
status dashboard for:

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

Use the optional-args field before clicking a script button, e.g.:

- `--with-frontends --seed` for `dev-up.sh`
- `--profile full` for `dev-dummy-data.sh`
- `--deep --logs` for `host-check.sh`
