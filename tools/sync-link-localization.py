"""Apply the link workflow's authored translations to the existing dictionaries."""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TRANSLATIONS = {
    "en": {
        "Links": "Links", "MainLink": "Main link", "LinkConfiguration": "Link configuration",
        "CreateLink": "Create link", "ConfigureLink": "Configure link", "ActiveLink": "Active link",
        "LinkName": "Link name", "LinkNamePlaceholder": "e.g. Whole home",
        "LinkChooseDevices": "Select the speakers for this link.", "SaveLink": "Save link",
        "Cancel": "Cancel", "PlayLink": "Play to link", "NoActiveLink": "Choose a link",
        "LinkEmpty": "Create a link, add your speakers, then play music to it.",
        "LinkSummary": "{0} speakers · {1} available",
        "LinkNameRequired": "Enter a link name (1–48 characters).",
        "LinkNameExists": "A link with this name already exists.",
        "LinkDevicesRequired": "Select at least one speaker for this link.",
        "LinkReady": "Link ready. Press Play to start music.",
        "CreateLinkFirst": "Create a link and select its speakers before playing.",
        "LinkOffline": "None of this link's speakers are available. Rescan or edit its configuration.",
        "DeviceOffline": "Offline"
    },
    "zh-Hans": {
        "Links": "链接", "MainLink": "主链接", "LinkConfiguration": "链接配置",
        "CreateLink": "创建链接", "ConfigureLink": "配置链接", "ActiveLink": "当前链接",
        "LinkName": "链接名称", "LinkNamePlaceholder": "例如：全屋音乐",
        "LinkChooseDevices": "选择要加入此链接的扬声器。", "SaveLink": "保存链接",
        "Cancel": "取消", "PlayLink": "播放到链接", "NoActiveLink": "选择链接",
        "LinkEmpty": "创建链接，添加扬声器，然后将音乐播放到此链接。",
        "LinkSummary": "{0} 个扬声器 · {1} 个可用",
        "LinkNameRequired": "请输入链接名称（1–48 个字符）。",
        "LinkNameExists": "已存在同名链接。", "LinkDevicesRequired": "请为此链接选择至少一个扬声器。",
        "LinkReady": "链接已就绪。点击播放开始音乐。",
        "CreateLinkFirst": "播放前请创建链接并选择扬声器。",
        "LinkOffline": "此链接的扬声器均不可用。请重新扫描或修改配置。", "DeviceOffline": "离线"
    },
    "ja": {
        "Links": "リンク", "MainLink": "メインリンク", "LinkConfiguration": "リンク設定",
        "CreateLink": "リンクを作成", "ConfigureLink": "リンクを設定", "ActiveLink": "再生先リンク",
        "LinkName": "リンク名", "LinkNamePlaceholder": "例：家全体",
        "LinkChooseDevices": "このリンクに追加するスピーカーを選択します。", "SaveLink": "リンクを保存",
        "Cancel": "キャンセル", "PlayLink": "リンクで再生", "NoActiveLink": "リンクを選択",
        "LinkEmpty": "リンクを作成してスピーカーを追加し、音楽を再生します。",
        "LinkSummary": "スピーカー {0} 台 · 利用可能 {1} 台",
        "LinkNameRequired": "リンク名を入力してください（1〜48 文字）。",
        "LinkNameExists": "同じ名前のリンクが既にあります。", "LinkDevicesRequired": "スピーカーを1台以上選択してください。",
        "LinkReady": "リンクの準備ができました。再生を押してください。",
        "CreateLinkFirst": "再生前にリンクを作成し、スピーカーを選択してください。",
        "LinkOffline": "このリンクのスピーカーは利用できません。再検索または設定を変更してください。",
        "DeviceOffline": "オフライン"
    }
}

for language, additions in TRANSLATIONS.items():
    path = ROOT / f"src/Musync.App/Assets/Languages/{language}.json"
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    data.update(additions)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print("Link workflow translated in English, Simplified Chinese and Japanese.")
