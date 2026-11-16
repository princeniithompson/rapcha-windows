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

Double-click the ring. The screen freezes, dims a little and its edges glow, like Circle to Search. Then use any of three gestures (the pill at the bottom always says which one you are in):

- **Tap** a word: click and let go without moving. After about a quarter of a second Rapcha reads the whole paragraph that word is in, from the start of the paragraph. No long press is needed, and a touchpad tap-to-click works too.
- **Drag a box**: press, move, and let go. Reading starts as soon as you let go.
- **Two corners**: click once and let go, then start moving the pointer straight away. The pill says "Corner 1 set: move and click again" and a green box follows the pointer without holding the button, for as long as you like (pausing never starts reading). Click a second time to read. Esc or a right click cancels.

A green box then moves along word by word. Use **Back 10**, **Pause/Play**, **Forward 10** and **Stop** at the bottom (or Space and the arrow keys). Press **Esc**, click the **X**, or double-click the ring to go back. If there is no text there it says "No text found there."

Like Android Select to Speak, every line your box touches is read **whole**, from its first word to its last, even if the box is a thin strip over the start of the lines; nothing is ever cut at the edge of the box. Lines above or below the box (a title) are not read unless the box touches them, and a strip in one column never reads the next column. Rapcha reads like a page of a book: columns left to right, then top to bottom, line by line. Words that the text recognition saw twice (in two passes or two overlapping slices) are said once. Code such as `:app:testDebugUnitTest` is read as "app test Debug Unit Test", while the green box stays on the token.

Speed: as soon as the overlay opens, Rapcha reads the whole screen once quickly and then keeps improving that reading in the background, at low priority, while you position the pointer. When you let go it uses what is already there, so speech starts within a fraction of a second. The voice is started once when Rapcha starts and kept ready, and long text is spoken in sentence-sized pieces where the next piece is prepared while the current one plays, without gaps. To find faint, small, coloured or white-on-dark text, the background reading looks at the screen several ways and merges them: as it is, a grey copy with dark areas turned light, a sharpened black-and-white copy, and enlarged copies in slices. Timings (only numbers, never words) are written to `%AppData%\Rapcha\rapcha.log`.

In **Settings → Select to speak** the voice, Preview and speed are locked so they can't be changed by accident: press **Edit**, change them, then **Done** to save. **Reset** (after you confirm) puts back the Windows default voice and normal speed, and changes nothing else.

Text is found with the offline text recognition built into Windows, and read with the built-in Windows voice. Nothing is downloaded and nothing leaves your PC. (Older versions downloaded a Piper voice to `%AppData%\Rapcha\piper`; Rapcha now deletes it to free the space.)

## For developers

C# WinForms on .NET Framework 4.8 (already part of Windows 10 and 11), one small portable `Rapcha.exe` with no extra DLLs.

- `src/Rapcha/Core` – the Gemini Live protocol, prompt and transcript logic, ported from the Android app.
- `src/Rapcha/Win` – the floating ring, microphone, typing and settings.
- `tests/Rapcha.Tests` – unit tests for the logic (run anywhere with `dotnet test tests/Rapcha.Tests`).

Build with `dotnet build src/Rapcha -c Release`. New versions are built and released automatically when code is pushed to main.
Settings are kept in `%AppData%\Rapcha`; the API key is encrypted for your Windows account only.
