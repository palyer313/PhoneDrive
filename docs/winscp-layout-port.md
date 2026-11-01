# WinSCP Commander 布局移植说明

依据官方 source/forms/ScpCommander.dfm 实际资源，保留 Local/Remote 两个独立面板、历史与导航工具栏、路径工具栏、文件操作工具栏、路径条、Report 文件列表、AddParentDir、独立状态栏、5px Splitter 和传输队列的结构；使用 WPF 重新实现并接到 PhoneDrive 的设备和传输服务。

当前布局对应关系：LocalPanel/RemotePanel → LeftPaneView/RightPaneView；LocalHistoryToolbar/RemoteHistoryToolbar → 各侧前进/后退；LocalNavigationToolbar/RemoteNavigationToolbar → 上级/刷新；LocalPathToolbar/RemotePathToolbar → 数据源和路径导航；LocalFileToolbar/RemoteFileToolbar → 复制、移动、重命名、新建、属性；LocalDirView/RemoteDirView → GridViewRowPresenter 详细列表；LocalStatusBar/RemoteStatusBar → 两侧文件数量与选择状态。

原始 DFM 与许可证保存于 third_party/references/winscp/。Windows 版本的 GPLv3 COPYING、NOTICE 与完整源码一同提供。应用品牌图标原创；文件类型图标取自 Windows Shell，不复用 WinSCP 品牌和图标集。没有把 VCL/DFM 文件错误地当作可直接编译的 WPF 代码。
