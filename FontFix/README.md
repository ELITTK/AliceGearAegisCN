# Alice Gear Aegis 中文字体修补

当前安装：Alice Font Guard 0.5.0（2026-09-11）。
环境：Unity 2022.3.62f3，BepInEx 5.4.23.5，XUnity.AutoTranslator 5.5.1。

## 修复内容

- 原 OverrideFont=ugui_font:SourceHanSans 被当作系统字体名，加载失败。现为 Microsoft YaHei。
- 游戏部分位图字体的基准字号及 Text 字号为 0，直接用于创建动态字体后导致 4096 图集溢出。将非正字体创建字号及替换文本字号归一为 32（原字形 advance 采样也为 32）；保留已有有效字号。最大字形栅格尺寸默认 96。
- 菜单顶部五个标签已有文本，但原框高 30、字号 32、纵向 Truncate，导致完全不可见。0.3 对这些菜单标签允许纵向 Overflow，用户确认恢复。
- 0.4 增加通用补救：仅对微软雅黑短文本（最多 200 字符、无换行）、原生成成功却完全没有文字几何、且为纵向 Truncate 的组件，使用同一个 TextGenerator 再以 Overflow 生成一次。仅成功生成几何时使用补救结果，否则恢复原生成参数。正常可见文字及多行正文保持原布局。重试有重入保护，诊断最多记录 80 个组件。
- 原生 UGUI RawImage 在未设置独立纹理时，会读取材质 mainTexture。Pyd/UI/SimpleFxAnim1P 实际使用 _AlbedoTex，读取 _MainTex 导致报错。只对这个已确认的着色器读取实际 _AlbedoTex；对 Common/LoadingEffect/MiniLoadingFx 沿用原有白纹理回退，但不查询不存在的属性。已有独立纹理及其他着色器不变。
- 0.4.1 根据运行日志和游戏 UGUI 代码将“完全没有几何”判据精确为零顶点：当前引擎每个字符四个顶点，没有旧版尾部空四边形。这样也支持缺失的单字标签，不会对正常显示的单字做额外重试。

## 验证

- 0.4 实际运行：用户确认多人任务页底部文字与菜单五个标签均正常。
- 同次日志记录 45 个文本组件获得补救，包括返回/主页按钮、多人准备文字、编队角色名及若干数值标签；返回/主页由 0 顶点变为 8 顶点。
- 同次完整日志 Error 计数为 0，两种自定义材质兼容路径均触发；此前图集错误及 _MainTex 错误均未再出现。
- 0.4.1 已编译并安装，安装文件与编译产物哈希一致。最后的零顶点判据修正尚未单独进行新一轮游戏实测；除单字/空白判据外与已测试的 0.4 一致。
- 尚未穷尽所有剧情、所有菜单和长时间运行，不能保证全游戏不存在其他缺字或布局问题。

## 文件与设置

插件：BepInEx/plugins/AliceFontGuard/AliceFontGuard.dll
源码：FontFix/AliceFontGuard.cs
构建：FontFix/build.ps1（关闭游戏后运行；系统 .NET Framework 编译器及已有游戏依赖，无需下载）

## 两阶段字体方案

当前插件的第一阶段是“按字形覆盖率路由”：Text 的原始静态 bitmap Font 能覆盖整段文字时保留原字体和原始字号；只有缺少字形时才切换到 YaHei。这样能保留原字体的基线、字距和装饰字形，避免用一个全局字号覆盖所有界面。

第二阶段是资源级混合字体。`tools/analyze_fonts.py` 会只读扫描本地缓存的 Unity 资源，输出 `tools/font_inventory.json`，记录 Font 的 CharacterInfo 数量、Unicode 范围、行高、材质和图集尺寸。确认图集结构后，再制作运行时克隆 Font：复制原始 CharacterInfo/材质，向单独的 CJK 图集中追加中文字形，只让缺字的字符走追加图集。原始 `deco_font` 保持不改，避免把按钮装饰图当作普通文字。

资源分析不直接写回加密 AssetBundle；这样即使分析失败也不会破坏游戏缓存。只有在确认字体页数、材质采样和 glyph advance 后，才会进入运行时混合 Font 实现。
配置：BepInEx/config/local.alice.fontguard.cfg
翻译配置：BepInEx/config/AutoTranslatorConfig.ini

[Font] ZeroSizeFallback=32，MaxRasterSize=96。
[Layout] MenuLabelOverflow=true，RecoverClippedLabels=true。
[Images] FixCustomRawImages=true。
各项改动可独立关闭，配置更改重启生效。
BepInEx WriteUnityLog 已开启，0.2.1 的全局材质诊断钩子已移除。

## 备份与回退

- FontFixBackup/20260910-171204：首次修改前配置和日志。
- FontFixBackup/20260910-171726-diagnostics：早期诊断记录。
- FontFixBackup/20260910-175803-menu-labels：0.3 前文件及菜单修复验证。
- FontFixBackup/20260910-181224-general-clipping：0.4 前文件、已实测 0.4 的源码及 DLL、v04-verified-no-errors.log。

关闭游戏后可将备份 DLL 放回插件路径进行版本回退。
要完全停用插件，将 DLL 移到 BepInEx/plugins 外，同时清空翻译配置 OverrideFont，避免失去零字号保护后重新触发图集错误；中文可能再次缺字。
只回退最新微调可使用备份中的 AliceFontGuard-v040.dll。
原始游戏 DLL 和汉化文本未修改。

用户偏好：不允许操作窗口、鼠标和键盘。只通过相关文件和日志排查，截图与界面操作由用户完成。
## 0.5.0 英文副标签与数字排版

- 用户提供关闭覆盖字体后的编队页对照图，确认 WEAPON / DRESS GEAR、BACKUP CHARACTER 及底部英文副标签是原界面的小字；MAX、Lv. 等也有原生数字字体排版。此前把位图文本统一改成 32 号微软雅黑，破坏了这些布局。
- 新增 Font.PreserveNativeAscii=true：替换前保存控件原位图字体和字号；仅当当前文本是纯可打印 ASCII、无富文本标签，且所有字符均存在于原字体字形表中时，保留原字体及原字号。原始字号 0 对位图字体有效，不会用于创建动态字体。
- 监听 Text.text 更新：从英文／数字变为中文、含非 ASCII 字符或原字体缺字时，自动切回已保存的微软雅黑和有效字号；变回原字体可完整显示的 ASCII 时恢复原排版。显式恢复原字体会停止自动切换，避免干扰字体开关。
- 使用弱引用表管理控件／字体状态，不逐帧扫描、不新增 TextGenerator 或纹理生成。保留菜单、页脚空白补救及材质兼容修复。最多记录 100 条 NATIVE ASCII 诊断。
- 编译通过，已检查 Harmony 目标方法及参数签名，安装 DLL 与暂存产物 SHA256 一致。尚未在游戏中验证 0.5.0 的实际显示及中英文切换。
- 本次旧 DLL、源码、字体覆盖配置及说明备份于 FontFixBackup/20260911-103159-native-labels。回退时关闭游戏，将备份 AliceFontGuard.dll 放回插件目录即可。
- 检查项目：编队页六个栏位副标签、底部三个副标签、MAX/Lv. 数字不重叠；角色中文名、菜单五个标签、返回／主页依然正常。
