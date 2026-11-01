# PCL VD 路线图

> 目标：UI 尽可能接近 PCL2（可达到 ~95%，100% 不可能），功能上做扎实的下载器。
> 原则：能搬 PCL 的模板就搬，不手写复刻；每阶段都要有可验证的产出。

更新时间：2026-11-01

---

## 一、现状盘点

### 已完成

| 模块 | 状态 |
|---|---|
| 原版下载 1.13+（含 26.x 新命名） | ✅ 实测 26.3 / 1.13.2 / 1.14.4 |
| Fabric / Forge / NeoForge 安装 | ✅ 三种加载器端到端通过 |
| 数据源切换（官方 / BMCLAPI / 自动） | ✅ |
| Java 运行时自动配置 | ✅ java-runtime-epsilon 25 |
| Modrinth 资源下载（模组/光影/资源包） | ✅ 含依赖解析、去重、图标 |
| `.mrpack` 整合包 | ✅ Fabulously Optimized 51 文件 + 57 overrides |
| 中文名检索（WikiEntries 26k 条） | ✅ 机械动力→create 等 |
| 图标 / 字体 | ✅ 搬了 PCL 的 Path 几何与 `PCL English` |
| 许可合规 | ✅ 署名、蓝色主色、PCL 开头命名、公开仓库 |
| 仓库 | https://github.com/FangZongHWo/pcl-vd |

### 已知差距（用户反馈：UI 仍差较多）

| # | 差距 | 原因 |
|---|---|---|
| 1 | 下拉框、滚动条、复选框不是 PCL 的观感 | 全局模板是手写的，没搬 `Application.xaml` |
| 2 | 列表项行高、hover、选中态不像 | 没移植 `MyListItem` |
| 3 | 按钮尺寸/状态/按压动画不对 | 没移植 `MyButton` |
| 4 | 没有加载动画、没有提示气泡 | 没移植 `MyLoading` / `MyHint` |
| 5 | 版本列表行没有左侧图标 | 未做 |
| 6 | 设置项塞在下载页右栏 | 没有独立设置页 |

---

## 二、阶段计划

### 阶段 1：全局控件模板真移植（0.5 天）

**动作**
- 取 PCL `Plain Craft Launcher 2/Application.xaml`（519 行）中标准控件的 `ControlTemplate`：
  `ComboBox`、`ComboBoxItem`、`ScrollBar`、`Thumb`、`ScrollViewer`、`ToggleButton`、`TextBlock`、`Window`
- 剔除所有 `local:` 自定义控件条目（`local:MyComboBox`、`local:MyScrollBar` 等，我们没有这些类型）
- 资源键对齐：PCL 用 `ColorBrush1..8`，我们已同名；差异键补别名
- 替换掉 `src/Mcdl.Wpf/App.xaml` 里手写的那几段

**验收**：下拉展开截屏、滚动条 hover 截屏，与 PCL 同尺寸截图叠放对比，边框/圆角/阴影位置一致。

---

### 阶段 2：列表项移植（0.5 天）

**动作**
- 读 `Controls/MyListItem.xaml`（2 KB）+ 它的 `.vb`，把视觉结构（左图标槽、标题、副标题、右侧标签、hover 渐变、选中态）翻成我们自己的 `PclListItem`
- 版本列表行加左侧图标（原版 / Fabric / Forge / NeoForge 的区分图标）
- 行高按 PCL 的密度（约 26–30）

**验收**：版本列表截图与 PCL 下载页左栏对比，行高、hover、选中竖条一致。

---

### 阶段 3：按钮与反馈组件（1 天）

**动作**
- `MyButton`：三种 ColorType（Highlight / Red / Black）、高度、内边距、hover/press 动画参数照搬
- `MyIconButton` / `MyIconTextButton`：图标按钮（标题栏、列表行操作都用得上）
- `MyLoading`：加载转圈（替换现在的自制弧线）
- `MyHint`：悬浮提示气泡（替换状态栏文字提示）

**验收**：按钮三态录屏/截帧；加载时出现 PCL 同款转圈；错误提示以气泡弹出。

---

### 阶段 4：页面结构调整（1.5 天）

**动作**
- 下载页按 `PageDownloadInstall.xaml`（34 KB）的分区重排：版本信息 / 安装方式选项卡 / 进度
- 新增「设置」页，把数据源、线程数、Java 路径等从下载页拆出去（对齐 PCL 五页结构中的「设置」）
- 顶部胶囊标签从 3 个变 4 个：下载 / 资源 / 设置 / 关于

**验收**：四个页签都能切换且动画正常；设置项不再挤在下载页。

---

### 阶段 5：图形资源补齐（0.5 天）

**动作**
- 把 `ModBase.vb` 里剩余的 23 个图标常量全部导出成 XAML 资源（当前只用了 4 个）
- 关于页、列表行、按钮统一改用这套图标

**验收**：界面里不再出现“文字代替图标”的地方。

---

### 阶段 6：工程收尾（1 天）

**动作**
- 给 Core 补单元测试（版本号比较、规则求值、WikiEntries 匹配、mrpack 解析）
- 决定 Avalonia 工程去留（保留为跨平台分支 / 归档）
- README 更新（新截图、构建说明、目录说明）
- 打 Release、写 CHANGELOG

**验收**：`dotnet test` 通过；Release 页面有可下载的 exe + 许可文件。

---

## 三、工时汇总

| 阶段 | 内容 | 工时 | 累计 |
|---|---|---|---|
| 1 | 全局控件模板 | 0.5 | 0.5 |
| 2 | 列表项 | 0.5 | 1.0 |
| 3 | 按钮与反馈 | 1.0 | 2.0 |
| 4 | 页面结构 | 1.5 | 3.5 |
| 5 | 图标补齐 | 0.5 | 4.0 |
| 6 | 工程收尾 | 1.0 | 5.0 |

到阶段 4 结束预计视觉还原度 ~93%；阶段 5 后 ~95%。

---

## 四、明确不做的（除非另行要求）

| 项 | 原因 |
|---|---|
| 启动游戏 | 需要参数拼装、natives 解压、账号体系，是另一个量级；当前定位是下载器 |
| CurseForge 数据源 | 需要 API key，且条款限制第三方分发 |
| 数据包安装 | 必须指定具体世界，UI 交互另做 |
| （暂缓）页面切换的完整版 PCL 动画 | 依赖它的 `ModAnimation` 模块（VB）。**已确认以后做**，见阶段 7 |

---

### 阶段 7（暂缓，已规划不做）

**完整版 PCL 页面切换动画**：搬 `ModAnimation.vb` 的 Storyboard 封装与页面进出场曲线，
替换现在这版简化的淡入+位移。等前 6 个阶段收尾后再评估。

---

## 五、已确认的决定（2026-11-01）

1. **Avalonia 工程**：写进 `.gitignore`，磁盘文件保留，不进仓库、不再维护
2. **设置页**：数据源 / 下载线程数 / Java 路径 / .minecraft 路径 / 镜像开关；
   这些项从下载页与顶部目录栏移出，其他页面不再重复
3. **GitHub Release**：要发，附件挂自包含 exe + 许可文件

## 五（原）、待确认

1. Avalonia 工程（`src/Mcdl.App`）保留还是归档？
2. 阶段 4 的「设置」页要放哪些项？（建议：数据源、下载线程数、Java 路径、镜像开关）
3. 是否要发 GitHub Release（含 68.9 MB 的 exe）？

---

## 六、阶段 8：整合包导入（规划，未实施）

现状：只能从 Modrinth 搜索后在线安装；三种导入路径都没有。

### 8.1 本地 `.mrpack` 导入（1 天）

**现状代码**（`ModpackInstaller.InstallAsync`）是一条直线：
下包 → 解压读 `modrinth.index.json` → 装原版 → 装加载器 → 下 files → 铺 overrides → 写 `.mcdl-modpack.json`。
第 2 步之后的逻辑对"本地文件"完全通用，只有第 1 步（下包）绑死了 Modrinth。

**改动**
- 把 `InstallAsync(pack, version, ct)` 拆成：`EnsureArchiveAsync(...)`（下载，只有线上安装走）+ `InstallFromArchiveAsync(string mrpackPath, string? instanceName, ct)`
- 线上安装 = `EnsureArchiveAsync` → `InstallFromArchiveAsync`；本地导入 = 直接 `InstallFromArchiveAsync`
- 实例名：默认取 `index.name`（去非法字符），冲突时加 `-2`；UI 允许用户改名
- 已存在同名实例 → 提示"覆盖 / 换名 / 取消"

**UI**
- 资源页左栏顶部加「导入 .mrpack…」按钮 → 文件选择器（`.mrpack`）
- 进度复用现有 `ResourceProgressValue`

**验收**：拿一个手工下载的 `.mrpack`（如 Fabulously Optimized）导入，实例目录内容与线上安装完全一致；覆盖同名实例时二次确认。

### 8.2 拖拽导入（0.25 天，与 8.1 共用逻辑）

- `Window.AllowDrop="True"`，`DragOver` 校验扩展名（`.mrpack` / `.zip`）并给视觉反馈（整窗高亮边框）
- `Drop` → 按扩展名分发到 8.1 或 8.3
- 多个文件时只取第一个，其余提示忽略

**验收**：把 `.mrpack` 拖进窗口任意位置即开始导入；拖不支持的格式时有明确提示且不进入导入。

### 8.3 CurseForge `.zip` 整合包（1.5–2 天）

**格式差异**：`manifest.json` 里是 `{ minecraft: { version, modLoaders: [{ id: "forge-47.2.0" }] }, files: [{ projectID, fileID }] }`，
只有 ID，没有下载地址；真实地址要查 CurseForge API，且 CDN 链接有时效。

**分两档做**

档 A（无 key，1 天）
- 解析 `manifest.json`，装对应原版 + 加载器（从 `modLoaders[0].id` 拆出 forge/fabric/neoforge 与版本）
- 铺 `overrides/`
- 生成**缺件清单**：把 `projectID/fileID` 列表写进实例目录的 `missing-mods.txt`，并在 UI 给出可点击的 CurseForge 页面链接

档 B（有 key，+0.5–1 天）
- 设置页加「CurseForge API Key」输入（存本地配置，不写进仓库）
- 用 `/v1/mods/{id}/files/{fileId}` 批量取真实下载地址 → 走现成的分段下载器
- 限流与 403 要处理（CF API 对无 key 请求直接拒绝）

**验收**：档 A 导入一个 CF 整合包后，实例能列出缺失模组清单；档 B 能完整装齐。

### 8.4 风险与边界

| 风险 | 处理 |
|---|---|
| 整合包里 `env.client = "unsupported"` 的文件 | 跳过，不下载（`MrpackEnv` 已解析，需确认逻辑生效） |
| 大包（数百 MB） | 复用分段下载器与现有进度回调，不另写下载 |
| 路径含中文/空格 | `SafeJoin` 已有，补一个带空格路径的回归测试 |
| 实例名冲突 | 8.1 的三选一提示 |
| CF 的 CDN 链接失效 | 下载失败时降级为"记入缺件清单"，不整包失败 |

### 8.5 工时汇总

| 子项 | 工时 |
|---|---|
| 8.1 本地 mrpack | 1.0 天 |
| 8.2 拖拽 | 0.25 天 |
| 8.3 CF 档 A（无 key） | 1.0 天 |
| 8.3 CF 档 B（有 key） | 0.75 天 |
| 合计 | 3.0 天 |