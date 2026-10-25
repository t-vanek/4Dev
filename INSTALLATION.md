# 4Dev — Installation

Get 4Dev running on one Windows PC in **4 steps**: install the tools, get the files, start the
servers, build and launch the game.

- The **servers** all run in **Docker**, so you don't build them yourself.
- The **game client** is built on your machine: with Visual Studio on Windows, or with clang on Linux.

---

## Step 1 — Install the tools

| Tool | Why you need it |
|---|---|
| **Windows 10/11 (x64)** | Runs the servers (Docker) and the game client. The client also runs on Linux: see Step 4. |
| **[Docker Desktop](https://www.docker.com/products/docker-desktop/)** | Runs the database and every game server. |
| **PowerShell 7.4 or newer** | Runs the local configuration script. |
| **[Git](https://git-scm.com/) + [Git LFS](https://git-lfs.com/)** | Downloads the project, including the big game files. |
| **Visual Studio 2022 or newer** (Community or Build Tools) with **Desktop development with C++** | Builds the game client. CMake and Ninja come with it. |

> Tip: from an **administrator** PowerShell, `scripts\install-build-deps.ps1` installs the Visual Studio
> part for you.

Everything else (DirectX SDK, SDL3, bgfx, zlib…) is already included in the repo.

---

## Step 2 — Get the files

```powershell
git clone https://github.com/t-vanek/4Dev.git
cd 4Dev
git lfs install
git lfs pull
```

**Don't skip `git lfs pull`.** Without it, some game files are just tiny placeholders and the game
crashes as soon as it starts. To check, look at this file's size. It should be around **146 MB**, not
a few bytes:

```powershell
(Get-Item "Game\Data\Mesh\Character.TMH").Length
```

---

## Step 3 — Start the servers

1. **Prepare the local configuration** in PowerShell 7.4 or newer:

   ```powershell
   .\scripts\configure-local-server.ps1 -BackupDirectory 'C:\path\to\Database'
   ```

   The backup folder must contain `TGLOBAL_RAGEZONE.bak` and `TGAME_RAGEZONE.bak`.
   The script creates a random SQL password for a new installation, reusable TLS certificates for
   World/Map/Control, and encrypted SMTP delivery to Mailpit. For an existing database volume,
   first put its original password in `.env` as `SA_PASSWORD='your existing password'`.
   Existing credentials and valid certificates are preserved when the script runs again.

   `.env` and `.runtime/` contain private configuration and keys; Git ignores both. The script
   restricts access to `.runtime/`. Save these files privately if you want to preserve the identities.
   The default setup exposes ports only on `127.0.0.1`, for playing on this PC. It also reapplies
   the local client address after database migrations without editing migration history.

2. **Build and start everything:**

   ```powershell
   docker compose up -d --build
   ```

   The first start takes a few minutes. Docker creates the databases, sets them up, then starts the
   five game servers.

3. **Check that everything is up:**

   ```powershell
   docker compose ps
   ```

   `mssql-importer` should say **Exited (0)**. That's normal: it only runs once. All the other
   containers should say **Up**.

   Open [Mailpit](http://127.0.0.1:8025) to read local verification messages. Mailpit captures mail
   locally; it does not deliver messages to external mailboxes. SMTP uses TLS with certificate
   validation. Only the Login container trusts the generated Mailpit certificate.

4. **Optionally enable email 2FA for the seeded development account** (`test` / `test123`):

   ```powershell
   @'
   DECLARE @id int;
   SELECT @id=dwUserID FROM dbo.TACCOUNT_PW WHERE szUserID='test';
   IF @id IS NULL THROW 50001, 'Test account is missing.', 1;
   EXEC dbo.TLoginTwoFactorConfigure @dwUserID=@id, @szEmail=N'test@4dev.local', @bEnabled=1;
   '@ | docker exec -i araz-mssql /bin/bash -c '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -b -d TGlobal_gsp'
   ```

   Enter the six-digit code from Mailpit in the client's existing verification dialog. Other
   accounts are not enrolled automatically. For an external SMTP provider, replace the local
   Login SMTP configuration with that provider's TLS settings and verified account recipients.

Rebuilds and `docker compose down` preserve SQL data. Do not use `down -v` unless you intend to
delete the database and captured messages. Docker manages container start/stop; Control connects
to World and Map for monitoring and GM commands. Its local process controller does not manage
other containers. Patch downloads are unconfigured in this local source-build installation.

### What's running

| Server | Service | Port |
|---|---|---|
| Database (SQL Server 2022) | `mssql` | 11433 |
| Login (the game connects here first) | `loginsvr` | 4816 |
| World (internal, mutual TLS) | `worldsvr` | 3816 |
| Map (the game world itself) | `mapsvr` | 5816 |
| Patch | `patchsvr` | 3716 |
| Control | `controlsvr` | 3615 |
| Mailpit web mailbox | `mailpit` | 8025 |
| Mailpit SMTP (internal, TLS) | `mailpit` | 1025 |

---

## Step 4 — Build and launch the game

The game client runs on **Windows** and on **Linux**. Pick your section.

### Windows

1. **Build the client** (from the project root):

   ```powershell
   .\Client\build.ps1 -Deploy
   ```

   This builds the game and copies `TClient.exe` into the `Game\` folder.

2. **Launch it.** The game needs the server address and port:

   ```powershell
   cd Game
   .\TClient.exe 127.0.0.1 4816
   ```

   Windows asks for **administrator** permission. Accept it.

3. **Log in** with the test account: **`test`** / **`test123`** 🎉

> The window size is set in `Game\config.ini` (`ScreenX` / `ScreenY`, 1600×1200 by default).

### Linux

Tested on Ubuntu 24.04 (and in WSL). Debian 13 works the same way; on Debian 12, add the
[apt.llvm.org](https://apt.llvm.org/) repository first, since its own clang is too old.

1. **Install the build tools** (clang 18, CMake, Ninja and the SDL3 headers). This uses `sudo`:

   ```bash
   ./Client/build-linux.sh --deps
   ```

2. **Build the client:**

   ```bash
   ./Client/build-linux.sh
   ```

   The game is built in `~/build/tclient-linux/bin/TClient`. To use another clang version, prefix
   both commands with it, e.g. `LLVM=19 ./Client/build-linux.sh`.

3. **Launch it** from the `Game` folder, with a `config.ini` next to the binary:

   ```bash
   cp Game/config.ini ~/build/tclient-linux/bin/
   cd Game
   ~/build/tclient-linux/bin/TClient 127.0.0.1 4816
   ```

   Use the IP of the PC running the servers if it's another machine.

4. **Log in** with **`test`** / **`test123`** 🐧

> In WSL the game runs on your GPU through Windows, and picks the right settings by itself. It is
> slower than native Linux, and turning the camera with the mouse is limited.

### Choosing the renderer (`BGFXRenderer`)

The game draws through **bgfx**, which can talk to your graphics card in several ways. You choose
in `config.ini`, in the `[Settings]` section:

```ini
BGFXRenderer=Auto
```

| Value | What it uses |
|---|---|
| `Auto` (default) | The best one for your system. Keep this unless something looks wrong. |
| `D3D11` / `D3D12` | Direct3D (Windows only). |
| `Vulkan` | Vulkan (Windows and Linux). |
| `OpenGL` | OpenGL 4.3 or newer (Windows and Linux; used in WSL). |

If the value isn't available on your PC, the game falls back to `Auto`. For a quick test without
touching the file, set the `TCLIENT_RENDERER` environment variable to one of these values.

---

## Everyday use

| I want to… | Command |
|---|---|
| Start the servers | `docker compose up -d` |
| Stop the servers | `docker compose down` |
| See a server's logs | `docker compose logs mapsvr` (or any service name) |
| Rebuild the servers after a code change | `docker compose up -d --build` |
| Reset the databases to a clean state | `docker compose down -v` then `docker compose up -d` |

> `down -v` **deletes all game data** (characters, items…) and rebuilds the databases from scratch.

---

## Something's wrong?

| Problem | Fix |
|---|---|
| The game crashes right at startup | The big files are missing. Run `git lfs pull` (Step 2). |
| The game opens then closes | Launch it with the address: `TClient.exe 127.0.0.1 4816`. |
| Login works but the game hangs or can't enter the world | Re-run `scripts/configure-local-server.ps1`, apply the local topology with `docker compose run --rm mssql-importer`, then `docker compose restart loginsvr patchsvr`. Check Map/World logs if it persists. |
| Servers don't start | Read `docker compose logs mssql-importer` and the affected server's logs. Correct the reported configuration or migration error, then run `docker compose up -d`. Preserve the database volume. |
| Nothing works after a reboot | Docker Desktop isn't running, or the containers are stopped. Start Docker, then `docker compose up -d`. |
| Can't log in | Wait 1–2 minutes after `docker compose up`, since the servers load game data at boot. Check with `docker compose logs loginsvr`. |

---

Thanks Zemi, thanks Araz <3
