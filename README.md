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

A green box then moves along word by word. When several paragraphs or list items are picked, each gets a small number (1, 2, 3, in reading order) just outside its left edge, never on the text; you can turn the numbers off in Settings. Use **Previous**, **Pause/Play**, **Next** and **Stop** at the bottom. Press **Esc**, click the **X**, or double-click the ring to go back. If there is no text there it says "No text found there."

**Previous and Next** work like Android Select to Speak: Next jumps straight to the start of the next paragraph, list item or heading and reads it; on the last one it stops and the bar says "Finished". Previous starts the current paragraph again, or, if you press it within about one and a half seconds of the paragraph starting, goes back to the one before. After a tap, Next and Previous carry on into the paragraphs above and below on the screen. While paused they move the green box and wait for Play. Keys: Right or Page Down for Next, Left or Page Up for Previous, Space for Pause/Play. These keys only do this while something is being read or paused; at any other time they are left alone for your other apps.

Like Android Select to Speak, every line your box touches is read **whole**, from its first word to its last, even if the box is a thin strip over the start of the lines; nothing is ever cut at the edge of the box. Lines above or below the box (a title) are not read unless the box touches them, and a strip in one column never reads the next column. Rapcha reads like a page of a book: columns left to right, then top to bottom, line by line. Words that the text recognition saw twice (in two passes or two overlapping slices) are said once. Code such as `:app:testDebugUnitTest` is read as "app test Debug Unit Test", while the green box stays on the token.

Speed: as soon as the overlay opens, Rapcha reads the whole screen once quickly and then keeps improving that reading in the background, at low priority, while you position the pointer. When you let go it uses what is already there, so speech starts within a fraction of a second. The voice is started once when Rapcha starts and kept ready, and long text is spoken in sentence-sized pieces where the next piece is prepared while the current one plays, without gaps. To find faint, small, coloured or white-on-dark text, the background reading looks at the screen several ways and merges them: as it is, a grey copy with dark areas turned light, a sharpened black-and-white copy, and enlarged copies in slices. Timings (only numbers, never words) are written to `%AppData%\Rapcha\rapcha.log`.

In **Settings → Select to speak** the voice, Preview, speed and **Show number badges** are locked so they can't be changed by accident: press **Edit**, change them, then **Done** to save. **Reset** (after you confirm) puts back the Windows default voice, normal speed and number badges on, and changes nothing else. With number badges off the outlines, the reading order and Previous / Next are exactly the same.

Text is found with the offline text recognition built into Windows, and read with the built-in Windows voice. Nothing is downloaded and nothing leaves your PC. (Older versions downloaded a Piper voice to `%AppData%\Rapcha\piper`; Rapcha now deletes it to free the space.)

## Offline voice typing

When there is no internet, or it drops while you talk, Rapcha keeps typing with speech that runs on your laptop and keeps every word already captured. A small **Offline** tag shows in the words box, and the next dictation goes back to Gemini as soon as you are online. You can turn this off in **Settings → Offline voice typing → Offline fallback** (on by default).

At startup Rapcha checks the laptop's memory (also cores and free disk) and picks a level:

- **Low** (under 6 GB): built-in Windows speech, up to 5 minutes at a time. *Download Whisper model* is shown but greyed out: "Your laptop's memory is too low for this. Rapcha will use built-in Windows speech instead." Below it, **Download lightest model anyway** lets you try Whisper Tiny after a warning that the laptop may get hot, slow down, and use a lot of memory and battery. With Tiny downloaded, offline voice typing uses Whisper (up to 2 minutes at a time); online still uses Gemini. **Delete model** takes it away again, and Rapcha goes straight back to built-in Windows speech.
- **Medium** (6 to 8 GB): you can download Whisper Tiny (74 MB), up to 2 minutes at a time.
- **Strong** (over 8 GB): Whisper Base (141 MB), or Small (465 MB) for the best accuracy, up to 5 minutes at a time.

Built-in Windows speech is the desktop speech recogniser that comes with Windows 10 and 11 (System.Speech dictation). It runs fully offline and, being Microsoft's own code, also works in Windows S mode. If your laptop doesn't have it, Settings shows **Get Windows speech**, which opens Windows Speech settings so you can download the speech pack for your language.

Whisper models come from the official whisper.cpp models on Hugging Face (`ggerganov/whisper.cpp`). The download shows progress and can be cancelled, is checked against the published SHA-256, and is kept in `%AppData%\Rapcha\models` (Delete removes it). The model is loaded only while you talk and freed right after. Audio is turned into text in pieces of about 30 seconds while you keep talking, so memory stays flat. The whisper.cpp engine is inside Rapcha.exe (a 32-bit DLL written to `%AppData%\Rapcha\engine` and loaded on first use; no other program is started). Windows in S mode refuses unsigned DLLs; then Rapcha says "Whisper can't run in Windows S mode; using built-in Windows speech".

Offline, your words get a light clean-up without any model: capitals at the start of sentences, a full stop at the end, "um" and "uh" removed, and your Custom Vocabulary spelled your way. Online, Gemini's smart polish is unchanged. (An optional small polishing model for Strong laptops is planned; it is shown as "coming later".)

**Recording timer.** A small m:ss timer sits in the words box's bottom row, between Cancel and the keyboard icon, in a light shade of your glow colour. In the last 20 seconds it gently counts down ("0:19 left") and then Rapcha stops by itself and keeps the text. Limits: online 9:30 (Google's Live API keeps one connection for about 10 minutes, and Rapcha needs a few seconds after you stop to finish the text: https://ai.google.dev/gemini-api/docs/live-session), built-in speech 5:00, Whisper 2:00 (Low, downloaded anyway, or Medium) or 5:00 (Strong).

**Microphone and offline accuracy (v18).** Rapcha records from the Windows default microphone (waveIn WAVE_MAPPER, never a loopback unless that is your Windows default) and turns whatever the driver delivers (16-bit or 32-bit float, 44.1 or 48 kHz, mono or stereo) into 16 kHz mono 16-bit with a proper low-pass resampler. Built-in Windows speech now listens to the Windows default microphone itself, the way Microsoft's apps feed it (if you picked another microphone in Settings, or the dictation switched from Gemini part-way, it gets Rapcha's audio with the same gentle boost Gemini gets). Whisper only gets the parts with speech (an energy check skips silence and steady noise, which made Whisper invent phrases), quiet speech is brought up to a normal level, pieces overlap by one second and repeated words are removed, and Whisper runs in English with its anti-invention settings (no context, no blank or non-speech tokens, temperature fallback, no-speech threshold, one segment for short dictations).

**Settings → Test microphone.** Records 3 seconds the same way voice typing does, shows the microphone's name, the format Windows runs it at and the format Rapcha opened, a level meter and what it heard, then plays back the 16 kHz mono audio the offline engines get. **Save as WAV** writes `%AppData%\Rapcha\mic-test.wav` only when you press it.

**Memory.** Rapcha no longer stops a dictation because Windows reports little free memory (a 4 GB laptop always does). It stops early only if Rapcha itself grows too big (over 1.6 GB with Whisper, 1 GB with built-in speech) or holds too much audio, and, for Whisper only, if Windows has under 100 MB free for 5 seconds in a row. Built-in Windows speech is never stopped for Windows' memory. After Rapcha stops by itself, a tap or shortcut in the next 3 seconds does not start a new dictation (it is usually your own "stop" arriving late).

## Snooze

Drag the ring to the middle of the screen and hold it still for about 5 seconds. A round **Snooze** circle appears in the middle; drop the ring on it and Rapcha hides for 19 minutes (change it in **Settings → Snooze**: 19 minutes, 1 hour, 2 hours, or any number of minutes). It comes back by itself, in the same spot. Quick drags across the screen never show the circle, and dropping the ring anywhere else docks it at the edge as usual.

To wake it early: right-click the Rapcha tray icon and choose **Wake Rapcha now** (it shows the time left), or press **Wake Rapcha now** on the Home page. Your shortcuts keep working while snoozed: pressing a voice-typing or select-to-speak shortcut wakes Rapcha and starts as usual. Opening Rapcha again (from the Start menu or the exe) also wakes it. The end time is saved, so a restart during a snooze stays snoozed until the time is up.

## Start menu and Windows Search

Rapcha can type into the Start menu / Windows Search box: it now recognises Start and Search (StartMenuExperienceHost, SearchHost; SearchApp / SearchUI on Windows 10) as a text field, and types your words there as one line (Enter in that box would open the top result). Use your voice-typing shortcut there: Rapcha's ring and words box never take focus, so Start stays open while you talk. When Start or Search opens over the ring, Rapcha moves the ring (and the words box) beside it, and while you talk with Start open the tray icon shows a red listening dot.

What Windows doesn't allow: the Start menu, Search, Action Center and the taskbar's flyouts are drawn in their own Windows "bands" above every normal always-on-top window. Only signed accessibility apps with UIAccess, installed in Program Files, may draw above them, so Rapcha's bubble and its yellow field outline can't appear on top of Start. Sources: Microsoft, "Security Considerations for Assistive Technologies" (https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-securityoverview), Microsoft, "Only elevate UIAccess applications that are installed in secure locations" (https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-10/security/threat-protection/security-policy-settings/user-account-control-only-elevate-uiaccess-applications-that-are-installed-in-secure-locations), and ADeltaX, "Window z-order in Windows 10" (https://blog.adeltax.com/window-z-order-in-windows-10/).

## For developers

C# WinForms on .NET Framework 4.8 (already part of Windows 10 and 11), one small portable `Rapcha.exe` with no extra DLLs.

- `src/Rapcha/Core` – the Gemini Live protocol, prompt and transcript logic, ported from the Android app.
- `src/Rapcha/Win` – the floating ring, microphone, typing and settings.
- `tests/Rapcha.Tests` – unit tests for the logic (run anywhere with `dotnet test tests/Rapcha.Tests`).

Build with `dotnet build src/Rapcha -c Release`. New versions are built and released automatically when code is pushed to main.
Settings are kept in `%AppData%\Rapcha`; the API key is encrypted for your Windows account only.
