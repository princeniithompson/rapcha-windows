<h1 align="center">Rapcha for Windows</h1>

<p align="center">
  <a href="https://github.com/princeniithompson/rapcha-windows/releases/latest"><img src="https://img.shields.io/github/v/release/princeniithompson/rapcha-windows?label=version&style=flat-square&labelColor=EEF2F7&color=6B8499" alt="Latest version"></a>
  <a href="https://github.com/princeniithompson/rapcha-windows/releases/latest"><img src="https://img.shields.io/github/release-date/princeniithompson/rapcha-windows?label=updated&style=flat-square&labelColor=EEF2F7&color=6B8499" alt="Last updated"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-6B8499?style=flat-square&labelColor=EEF2F7" alt="Windows 10 and 11">
  <img src="https://img.shields.io/badge/RAM-4%20GB%20is%20fine-6B8499?style=flat-square&labelColor=EEF2F7" alt="4 GB RAM is fine">
</p>

<p align="center">
  Voice typing for Windows. Click the ring, speak, and your words are typed for you.
</p>

<p align="center">
  <a href="https://github.com/princeniithompson/rapcha-windows/releases/latest/download/Rapcha.exe"><img src="docs/images/download.svg" alt="Download for Windows" width="480"></a>
</p>

## How to install

1. Click **Download for Windows**.
2. Open **Rapcha.exe** when it finishes downloading. There is nothing to install.
3. If Windows says **"Windows protected your PC"**, click **More info** and then **Run anyway**.
4. Paste your Gemini API key in the window that opens and click **Save**.

Then click the red and white ring at the edge of your screen, speak, and click it again.
Your words are typed where your cursor is, or copied so you can press **Ctrl+V**.
Right-click the ring (or press **Esc**) while it is listening to cancel. Right-click it any other time for **Settings** and **Exit**.

## For developers

C# WinForms on .NET Framework 4.8 (already part of Windows 10 and 11), one small portable `Rapcha.exe` with no extra DLLs.

- `src/Rapcha/Core` – the Gemini Live protocol, prompt and transcript logic, ported from the Android app.
- `src/Rapcha/Win` – the floating ring, microphone, typing and settings.
- `tests/Rapcha.Tests` – unit tests for the logic (run anywhere with `dotnet test tests/Rapcha.Tests`).

Build with `dotnet build src/Rapcha -c Release`. New versions are built and released automatically when code is pushed to main.
Settings are kept in `%AppData%\Rapcha`; the API key is encrypted for your Windows account only.
