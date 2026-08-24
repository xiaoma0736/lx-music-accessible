# WPF accessibility semantics baseline — 2026-08-24

Issue: #34. Baseline commit: `bdddecada51cdc60beff1d2d7ab82dffb9a98021`.

## Scan coverage and result

The scan covered all 12 application XAML files (1,154 lines), their code-behind, custom automation peer, code-created menus, tray commands, user-visible strings, and the statically determinable WinForms installer controls. It found 232 direct XAML accessible names, 34 direct HelpText values, one ItemStatus binding, one live region, and 17 code-created `SetName` calls. The installer uses native `Text` labels and controls and had no explicit AccessibleName or AccessibleDescription of this class.

The deterministic baseline classified 178 direct names as overriding an existing native `Content`, `Header`, `Text`, or `Title`; 25 more names without static visible text contained a platform role; 20 HelpText values were redundant; and all 17 code-created names duplicated `Header`/`Content` or mixed a result message into an action name. Some entries overlap because a visible-name override can also contain a role.

A follow-up manual reader check found that the initial assumption about `Label.Target` was wrong: it associates focus but did not give the “按 Alt+F4 时” ComboBox an effective UIA Name. The corrected scan has 34 direct XAML Name attributes, 16 Label.Target relationships, two runtime SetName calls (only the two dynamic TextInputDialog fields), zero LabeledBy paths, and zero HelpText/Description/ToolTip sources.

A second manual reader check found the same class of false confidence in focused list state: the real source item peer returned `ItemStatus="已禁用"`, but its effective focused Name omitted that state and common screen readers did not announce ItemStatus. The three leaf-list ItemStatus bindings (source, hotkey, download) were therefore removed. Required compact state now occurs once in the item Name, while dynamic hotkey/download changes notify that Name. The current scan retains one dialog-level ItemStatus and two LiveSetting paths; neither is relied on as a focused leaf item's only state channel.

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
| `MainWindow.xaml`: SearchSourceCombo, SearchTypeComboBox, CategorySourceCombo, RankingSourceCombo, and settings inputs | explicit task names despite adjacent Label.Target, or role name “搜索输入框” | combo / edit | Manual reader verification and a real WPF peer repro proved `Label.Target` alone leaves the input UIA Name empty. Keep Label.Target for focus association, and use short explicit task names for all 13 labeled MainWindow inputs. SearchBox has no visible label and remains “搜索关键词”. |
| `MainWindow.xaml`: SearchButton and all other static buttons/check boxes | Content repeated; SearchButton specifically “搜索按钮” | button / check box | Remove. Native Content now yields “搜索” plus the platform role exactly once. Context-specific visible action text remains unchanged. |
| `MainWindow.xaml`: SearchResultsList, OnlinePlaylistList, FavoriteSongsList, CategoryList, CategoryPlaylistsListBox, CategorySongsList, RankingList, RankingSongsList, HotkeyListBox, DownloadTaskList, SourceList | names ending in “列表” | list | Rename to “搜索结果”, “在线歌单”, “在线歌曲”, “分类”, “歌单”, “歌单歌曲”, “排行榜”, “榜单歌曲”, “快捷键”, “下载任务”, and “音源接口”. Bound item names stay. |
| `MainWindow.xaml`: page-navigation controls hidden from UIA | explicit contextual names despite native Content | hidden button/text | Remove; the custom peer continues intentionally hiding them. |
| `MainWindow.xaml`: information panel and now-playing group | redundant container names | pane/group | Remove; named child values and Header remain. |
| `PlaylistImportView.xaml`: UserControl, headings/instructions/status, platform and action buttons | visible Text/Content repeated, often with “标题/提示/说明” | pane/text/button | Remove. Retain “Cookie” for the unlabeled edit and compact list names “我的歌单” and “歌单歌曲”. |
| `SongDetailDialog.xaml`: Window, title, DetailsList, close button | “歌曲详情对话框/标题/列表”; Content repeated | window/text/list/button | Use Title/Text/Content and rename the list to “歌曲详情”. |
| `SponsorQrWindow.xaml`: Window, title/instruction, close button, QR image | visible text repeated; image name includes “大图” plus an instruction | window/text/button/image | Remove visible duplicates; retain concise image name “赞助二维码”. |
| `TextInputDialog.xaml`: Window/title/buttons; Label1/2 and InputBox1/2 | generic “输入框一/二” and “标签” role words | window/text/button/edit/label | Remove generic role names. Keep Label.Target for focus association, and derive each input's runtime task Name from its dynamic label after removing the trailing colon. Real TextBox peers verify Name and Value remain separate. |
| `UserGuideWindow.xaml`: Window, headings/instruction, chapter Label/Combo, close button | visible values repeated or “标题/提示/标签” | window/text/label/combo/button | Remove visible duplicates; give the chapter combo the short task Name “章节” because Label.Target alone is not a peer Name source. Retain “使用说明正文” because the read-only TextBox is populated after construction and has no label. |
| `WebLoginWindow.xaml`: Window, platform/status text, three buttons | Title/Text/Content repeated | window/text/button | Remove. Dynamic status remains native text and is also announced through the live announcer workflow. |
| `HotkeyConfirmDialog.xaml.cs`, `HotkeyConflictDialog.xaml.cs` | result message concatenated into the focused button name | button | Delete SetName. The result stays in dialog text; action name stays “确定” or “重新设置”. |
| `MainWindow.xaml.cs`: 15 code-created song-menu items | SetName duplicates each MenuItem Header, including quality items | menu item | Delete SetName. Header remains the single native name source. |
| `HotkeySettingItem.AccessibleText` | action, shortcut, and ConflictStatus while the same state was also ItemStatus | list item | Keep action, shortcut, and compact availability/conflict state once in Name; remove the ItemStatus duplicate because it is not reliable on focus. |
| `MusicSource.AccessibleName` | source identity plus enabled/default/trust state | list item | Keep identity/version/type and required enabled/default/trust state once in Name; remove the ItemStatus duplicate. |
| `DownloadTask.AccessibleName` | task identity plus progress and status, duplicating non-downloading status twice | list item | Keep task identity plus the de-duplicated AccessibleStatus once in Name; notify Name on progress/status changes and remove the ItemStatus duplicate. |

## HelpText findings and zero-description policy

| Location | Baseline HelpText | Decision |
| --- | --- | --- |
| `HotkeyConflictDialog.xaml`: Window, two text blocks, two buttons | five descriptions restating the conflict, action, or visible message | Remove all; ItemStatus and visible dynamic text carry state. |
| `LyricsWindow.xaml`: LyricsList | full keyboard shortcut paragraph also visible below the list | Remove duplicate. |
| `MainWindow.xaml`: CloseBehaviorCombo, player/download/data fields, download check boxes, ClearCacheBtn | ten descriptions restating the label, Content, selected option, or nearby visible explanation | Remove all. |
| `MainWindow.xaml`: DownloadTaskList | browsing instruction plus where downloads are initiated | Remove; it does not change how the list itself is operated. |
| `PlaylistImportView.xaml`: both lists | instructions duplicate immediately adjacent visible text | Remove both. |
| `UserGuideWindow.xaml`: ChapterCombo and GuideText | ordinary arrow-key/read-only editing behavior already in visible instructions | Remove both. |
| `MainWindow.xaml`: Hotkey item HelpText binding | ConflictStatus duplicated as HelpText and ItemStatus | Remove HelpText. Later reader verification also removed the ItemStatus duplicate and restored compact conflict state to the single focused Name. |
| `MainWindow.xaml`: SearchBox and the seven song/playlist/category/ranking lists | Enter/Space/Delete operation teaching | Remove. Human screen-reader review confirmed these are basic platform/application operations and the extra description is noise. |
| `MainWindow.xaml`: CategorySourceCombo and RankingSourceCombo | Enter/Refresh operation teaching | Remove. The explicit network-action boundary remains in event behavior and visible workflow guidance, not HelpText. |
| `MainWindow.xaml`: HotkeyListBox | Enter/Delete/Esc operation teaching | Remove; visible instructions remain immediately above the list. |
| `MainWindow.xaml`: SourceList | application key, Enter, and Delete operation teaching | Remove; commands remain available through native menu/action behavior. |
| `SongDetailDialog.xaml`: DetailsList | Enter/Esc operation teaching | Remove; dialog controls and platform behavior remain named. |

Human review rejected the provisional 14-control exception set. The final allowlist is empty: application controls expose zero HelpText, AccessibleDescription, AutomationPeer help override, or ToolTip-derived UIA description. ItemStatus and LiveSetting remain valid state/event properties and are explicitly protected by negative diagnostic probes, but ItemStatus is not accepted as the only channel for task-required state on a focused leaf item.

## Regression boundary

The scan found 16 inputs that depended only on Label.Target: 13 in MainWindow, one in UserGuideWindow, and two dynamic fields in TextInputDialog. Fourteen static inputs now carry exact short task names; the two dynamic fields are the only allowed code-created SetName paths and are named from their runtime labels. STA regression tests use real ComboBox/TextBox AutomationPeers for all 16 inputs and verify that Name is non-empty and separate from Selection/Value. A dedicated negative probe locks in that Label.Target alone does not supply a ComboBox peer Name.

The remaining static tests reject visible-name overrides, role words for the corresponding control type, every HelpText/description/ToolTip source, all code-created SetName paths except the two dynamic dialog fields, and unnamed interactive controls. Positive name probes cover “搜索按钮”, “搜索标签页”, and “搜索输入框”; the name negative probe covers the real business term “播放列表”. HelpText and ToolTip positive probes are kept separate from ItemStatus and LiveSetting negative probes. Real STA peer tests cover the focused source states (enabled, disabled, default, and staged/untrusted), hotkey availability/conflict, download progress/status, leaf-child suppression, the absence of duplicate focused ItemStatus, and dynamic Name notifications. The live-region event path remains covered. Real keyboard and Narrator/NVDA/JAWS behavior remains the manual pre-release checklist.
