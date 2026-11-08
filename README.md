# MGU Setup

Windows desktop app for first-time root SSH setup and MGU Store installation using an ENET adapter.

- Leave the gateway address blank to discover it automatically. Use **Check ENET status** for a read-only connection check.
- For first-time SSH setup, run as Administrator, select the exact head-unit variant, and acknowledge the risk checkbox. Flashing clears coding and Digital Key pairings. Use an external coding tool to create any backup or recover coding.
- **Install MGU Store** uses existing root SSH and requires working vehicle coding. It finds the saved key by VIN or lets you select it. The app verifies the installer checksum and requests a normal head-unit restart when the installer reports one is needed.

Keep the complete tools folder beside MguSetup.exe. The bundled SSH helper includes its Python runtime. Windows OpenSSH is required. Store installation also needs internet access on the PC as it pulls directly from robertnisipeanu/mgu-store's github project.

Build the WPF project with .NET 9 using dotnet build. Keep private key material and vehicle data local when sharing builds or logs.

Upstream projects: https://github.com/cauliflowermossgrape/idrive7-8-ssh-root and https://github.com/robertnisipeanu/mgu-store


<img width="1303" height="994" alt="image" src="https://github.com/user-attachments/assets/4393f185-bd20-487a-be63-9dd9941848d0" />
