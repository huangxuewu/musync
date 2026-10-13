# Embedded Simplified Chinese font

Noto Sans SC is distributed under the SIL Open Font License 1.1. The full license is in `src/Musync.App/Assets/Fonts/NotoSansSC-OFL.txt` and is copied into the portable package.

Static regular (400), semibold (600) and bold (700) instances were generated with FontTools 4.66.1 from the official [Google Fonts variable font](https://github.com/google/fonts/tree/main/ofl/notosanssc). They retain the full character set and Noto family naming. Static files are used because the application's Avalonia font path does not support variable fonts.

Source SHA-256: `a3041811a78c361b1de50f953c805e0244951c21c5bd412f7232ef0d899af0da`.

Generation uses `fontTools.varLib.instancer.instantiateVariableFont(font, {'wght': weight}, inplace=True, updateFontNames=True)` for each weight, with timestamp recalculation disabled.
