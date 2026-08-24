# WPF accessibility semantics baseline — 2026-08-24

Issue: #34. Baseline commit: `bdddecada51cdc60beff1d2d7ab82dffb9a98021`.

## Scan coverage and result

The scan covered all 12 application XAML files (1,154 lines), their code-behind, custom automation peer, code-created menus, tray commands, user-visible strings, and the statically determinable WinForms installer controls. It found 232 direct XAML accessible names, 34 direct HelpText values, one ItemStatus binding, one live region, and 17 code-created `SetName` calls. The installer uses native `Text` labels and controls and had no explicit AccessibleName or AccessibleDescription of this class.

The deterministic baseline classified 178 direct names as overriding an existing native `Content`, `Header`, `Text`, or `Title`; 25 more names without static visible text contained a platform role; 20 HelpText values were redundant; and all 17 code-created names duplicated `Header`/`Content` or mixed a result message into an action name. Some entries overlap because a visible-name override can also contain a role.

## Name findings

| Location / elements | Baseline name or pattern | Platform role | Resolution and reason |
| --- | --- | --- | --- |
| `HotkeyConfirmDialog.xaml`: Window, MessageText, OkButton | “快捷键设置完成对话框”, “设置结果提示”, “确定” | window, text, button | Remove. `Title`, dynamic `Text`, and `Content` are the native names. |
| `HotkeyConflictDialog.xaml`: Window, MessageText, DetailText, both buttons | “快捷键冲突提示”, “冲突提示信息”, “冲突快捷键列表”, action content repeated | window, text, button | Remove. Keep ItemStatus because conflict is real state, not a description. |
| `KugouQrLoginWindow.xaml`: Window, title/status text, both buttons | Title/Text/Content repeated, including “窗口/标题” | window, text, button | Remove; retain only Image name “登录二维码”, which has no visible text source. |
| `LyricsWindow.xaml`: Window, title and instruction text | “歌词窗口”, “歌词标题”, “歌词窗口提示” | window, text | Remove and use Title/Text. Retain list name “歌词” and bound line names because list content is composed dynamically. |
| `MainWindow.xaml`: Window, all 5 top menus and 28 static menu items | Title/Header repeated; top items add “菜单” | window, menu item | Remove. WPF Header supplies the name and platform supplies the menu role. “播放列表” remains a valid business phrase in its Header. |
| `MainWindow.xaml`: SourceContextMenu and two playlist context menus | “××操作菜单” and child Header repeated | menu | Remove. Context and child Headers provide semantics. |
| `MainWindow.xaml`: MainTabControl and 8 TabItems | “主标签页”, “搜索标签页”, “播放列表标签页”, “导入个人歌单标签页”, “分类歌单标签页”, “排行榜标签页”, “设置标签页”, “下载管理标签页”, “音源管理标签页” | tab control / tab item | Remove. Tab Headers remain named; platform announces the tab role. |
| `MainWindow.xaml`: all static headings, instructions, counts, now-playing fields, status text and time | “××标题/说明/提示/信息” or Text repeated | text | Remove. Dynamic Text remains exposed through the native Text peer and live announcements remain separate. |
| `MainWindow.xaml`: all GroupBoxes and StatusBar | Header repeated or “××区域/状态栏” | group / status bar | Remove. Header supplies group names; status children expose current values. |
| `MainWindow.xaml`: SearchSourceCombo, SearchTypeComboBox, CategorySourceCombo, RankingSourceCombo, and settings inputs | explicit task names despite adjacent Label.Target, or role name “搜索输入框” | combo / edit | Use native Label.Target. SearchBox has no visible label, so rename it to “搜索关键词”. |
| `MainWindow.xaml`: SearchButton and all other static buttons/check boxes | Content repeated; SearchButton specifically “搜索按钮” | button / check box | Remove. Native Content now yields “搜索” plus the platform role exactly once. Context-specific visible action text remains unchanged. |
| `MainWindow.xaml`: SearchResultsList, OnlinePlaylistList, FavoriteSongsList, CategoryList, CategoryPlaylistsListBox, CategorySongsList, RankingList, RankingSongsList, HotkeyListBox, DownloadTaskList, SourceList | names ending in “列表” | list | Rename to “搜索结果”, “在线歌单”, “在线歌曲”, “分类”, “歌单”, “歌单歌曲”, “排行榜”, “榜单歌曲”, “快捷键”, “下载任务”, and “音源接口”. Bound item names stay. |
| `MainWindow.xaml`: page-navigation controls hidden from UIA | explicit contextual names despite native Content | hidden button/text | Remove; the custom peer continues intentionally hiding them. |
| `MainWindow.xaml`: information panel and now-playing group | redundant container names | pane/group | Remove; named child values and Header remain. |
| `PlaylistImportView.xaml`: UserControl, headings/instructions/status, platform and action buttons | visible Text/Content repeated, often with “标题/提示/说明” | pane/text/button | Remove. Retain “Cookie” for the unlabeled edit and compact list names “我的歌单” and “歌单歌曲”. |
| `SongDetailDialog.xaml`: Window, title, DetailsList, close button | “歌曲详情对话框/标题/列表”; Content repeated | window/text/list/button | Use Title/Text/Content and rename the list to “歌曲详情”. |
| `SponsorQrWindow.xaml`: Window, title/instruction, close button, QR image | visible text repeated; image name includes “大图” plus an instruction | window/text/button/image | Remove visible duplicates; retain concise image name “赞助二维码”. |
| `TextInputDialog.xaml`: Window/title/buttons; Label1/2 and InputBox1/2 | generic “输入框一/二” and “标签” role words | window/text/button/edit/label | Remove. Connect both dynamic labels with native Label.Target so the runtime label text names each input. |
| `UserGuideWindow.xaml`: Window, headings/instruction, chapter Label/Combo, close button | visible values repeated or “标题/提示/标签” | window/text/label/combo/button | Remove and use Label.Target. Retain “使用说明正文” because the read-only TextBox is populated after construction and has no label. |
| `WebLoginWindow.xaml`: Window, platform/status text, three buttons | Title/Text/Content repeated | window/text/button | Remove. Dynamic status remains native text and is also announced through the live announcer workflow. |
| `HotkeyConfirmDialog.xaml.cs`, `HotkeyConflictDialog.xaml.cs` | result message concatenated into the focused button name | button | Delete SetName. The result stays in dialog text; action name stays “确定” or “重新设置”. |
| `MainWindow.xaml.cs`: 15 code-created song-menu items | SetName duplicates each MenuItem Header, including quality items | menu item | Delete SetName. Header remains the single native name source. |
| `HotkeySettingItem.AccessibleText` | action, shortcut, and ConflictStatus while the same state is also ItemStatus | list item | Keep action and shortcut in Name; expose conflict only through ItemStatus. |
| `MusicSource.AccessibleName` | source identity plus enabled/default/trust state | list item | Keep identity/version/type in Name; bind StatusText to ItemStatus. |
| `DownloadTask.AccessibleName` | task identity plus progress and status, duplicating non-downloading status twice | list item | Keep task identity in Name; bind compact progress/status to ItemStatus. |

## HelpText findings and exceptions

| Location | Baseline HelpText | Decision |
| --- | --- | --- |
| `HotkeyConflictDialog.xaml`: Window, two text blocks, two buttons | five descriptions restating the conflict, action, or visible message | Remove all; ItemStatus and visible dynamic text carry state. |
| `LyricsWindow.xaml`: LyricsList | full keyboard shortcut paragraph also visible below the list | Remove duplicate. |
| `MainWindow.xaml`: CloseBehaviorCombo, player/download/data fields, download check boxes, ClearCacheBtn | ten descriptions restating the label, Content, selected option, or nearby visible explanation | Remove all. |
| `MainWindow.xaml`: DownloadTaskList | browsing instruction plus where downloads are initiated | Remove; it does not change how the list itself is operated. |
| `PlaylistImportView.xaml`: both lists | instructions duplicate immediately adjacent visible text | Remove both. |
| `UserGuideWindow.xaml`: ChapterCombo and GuideText | ordinary arrow-key/read-only editing behavior already in visible instructions | Remove both. |
| `MainWindow.xaml`: Hotkey item HelpText binding | ConflictStatus duplicated as HelpText and ItemStatus | Remove HelpText binding; retain ItemStatus as the actual state channel. |
| `MainWindow.xaml`: SearchBox and the seven song/playlist/category/ranking lists | Enter/Space/Delete changes task state and is not conveyed by the native role | Retain, shortened to the exact explicit actions. |
| `MainWindow.xaml`: CategorySourceCombo and RankingSourceCombo | selection alone intentionally does not fetch; Enter/Refresh is required | Retain to preserve the explicit network-action boundary. |
| `MainWindow.xaml`: HotkeyListBox | Enter/Delete/Esc are application-specific editing commands | Retain. |
| `MainWindow.xaml`: SourceList | application key, Enter, and Delete expose nonstandard source operations | Retain. |
| `SongDetailDialog.xaml`: DetailsList | Enter/Esc closes this nonstandard browse dialog | Retain. |

The retained HelpText set is fixed at 14 controls and enforced by `AccessibilitySemanticsTests`; adding another entry requires a task-specific reason in this document and the test allowlist.

## Regression boundary

The static tests reject visible-name overrides, role words for the corresponding control type, undocumented HelpText, code-created SetName calls, and unnamed interactive controls. Positive probes cover “搜索按钮”, “搜索标签页”, and “搜索输入框”; negative probes cover the real business term “播放列表”. Composite item names are tested separately from ItemStatus, and the live-region event path remains covered. Real keyboard and Narrator/NVDA/JAWS behavior remains the manual pre-release checklist.
