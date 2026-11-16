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

Then click the red and white ring at the edge of your screen and speak. Your words appear live in the Words bubble.
Click **Complete** (or the ring again) and your words are typed where your cursor is, or copied so you can press **Ctrl+V**.

If the bubble says **"I can't hear your microphone"**, click **Fix microphone** and turn on **Microphone access** and **Let desktop apps access your microphone**.
In Settings you can choose your microphone and watch the level bar move when you talk.
While you talk, the text box your words will go into glows (if there is none, the bubble says it will copy the words for you). When you stop, the ring spins until your words are in. Drag the ring or the Words bubble and they move together. Click **Cancel**, right-click the ring, or press **Esc** while it is listening to cancel. After 10 seconds with nothing to do the ring rests: smaller and dimmer at the edge. Tap it once to wake it. Right-click it any other time for **Settings** and **Exit**.

## Select to speak

Double-click the ring. The screen freezes, dims a little and its edges glow, like Circle to Search. **Drag a box around any text** (a bright green box follows your mouse or touchpad), or **click one corner and then the other**, or **tap a word** to hear its whole line. Rapcha reads exactly the text inside the box and a green box moves along word by word. Use **Back 10**, **Pause/Play**, **Forward 10** and **Stop** at the bottom (or Space and the arrow keys). When reading ends you can draw another box. Press **Esc**, click the **X**, or double-click the ring to go back. If there is no text in the box it says "No text found there."

Rapcha reads the screen like a page of a book: it finds columns, paragraphs, headings, list items and cards, and reads column by column, top to bottom. Your box snaps to what you meant: a block that is mostly inside the box (half its text, or 60% of its lines) is read whole, a block the box only grazes (under a quarter) is left out, and a box drawn tightly around a heading or a few lines reads just those. Before reading, the blocks it picked flash with soft green outlines and numbers 1, 2, 3 in reading order.

Text is found with the offline text recognition built into Windows, and read with the built-in Windows voice. Nothing is downloaded and nothing leaves your PC. Settings has a reading speed slider. (Older versions downloaded a Piper voice to `%AppData%\Rapcha\piper`; Rapcha now deletes it to free the space.)

## For developers

C# WinForms on .NET Framework 4.8 (already part of Windows 10 and 11), one small portable `Rapcha.exe` with no extra DLLs.

- `src/Rapcha/Core` – the Gemini Live protocol, prompt and transcript logic, ported from the Android app.
- `src/Rapcha/Win` – the floating ring, microphone, typing and settings.
- `tests/Rapcha.Tests` – unit tests for the logic (run anywhere with `dotnet test tests/Rapcha.Tests`).

Build with `dotnet build src/Rapcha -c Release`. New versions are built and released automatically when code is pushed to main.
Settings are kept in `%AppData%\Rapcha`; the API key is encrypted for your Windows account only.
