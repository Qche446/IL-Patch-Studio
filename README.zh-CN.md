# IL Patch Studio

[English](README.md)

IL Patch Studio 是一款用于查看托管 .NET 程序集并生成 MonoMod 补丁草稿的 Windows 工具。它并排显示反编译 C# 和原始 IL，可以从表达式、语句或指令生成可审查的 `ILCursor` 代码。打开程序集只读取元数据和方法体。

## 快速试用

1. 将 Windows x64 便携 ZIP 完整解压到可写目录，运行 `ILPatchStudio.exe`。便携版已包含 .NET 桌面运行时。
2. 打开发布包中的 `samples/PriceDemo.dll`，搜索 `Checkout.CalculateTotal`。如果使用源码，可用下文命令先编译示例。
3. 选中左侧的 `2.5f`，选择「替换常量」并输入 `3.0f`。右侧会显示匹配的 IL。
4. 选中计算小计的整条语句（随包示例中显示为 `float num3 = (float)num * num2;`），选择「语句后插入 C#」。点「传入变量」，选择小计对应的槽位并改为 `ref`。在草稿输入框里用界面显示的别名自增，例如 `local2++;`。
5. 将补丁方法复制到使用 MonoMod 的项目，在文件顶部添加所需的 `using`，再将补丁方法注册到目标方法的 IL 钩子。

## 补丁和依赖

- 可以替换常量或变换栈顶值，在语句、指令和调用前后插入 C#，也可以仅在条件跳转或顺序落入的路径执行。
- 可以变换调用结果、最后一个实参和字段读写值，也可以将选中的局部变量写入改到另一同类型槽位。
- 「传入变量」支持按值、`ref`、`out` 和 `in`。读取局部变量前请确认该路径已经赋值。`ref` 直接修改原位置，`out` 要求委托正常返回前赋值。
- 「包含 using」控制草稿是否带引用指令。关闭时界面会另列所需命名空间。匹配失败、重复命中和边界检查可按需配置。
- 「依赖」页会对比目标的直接程序集引用和解析器实际选中的文件。将鼠标悬停在条目上，可以看到请求身份、实际身份和路径。显示「版本不同」时，解析器可能仍会使用该文件。遇到缺失引用或反编译类型不明，可补充依赖搜索目录。

程序集加载和方法反编译在后台运行，快速切换时界面以最后选中的方法为准。生成器会检查锚点在当前 DLL 上是否唯一。实际使用前仍应核对目标版本和其他同时加载的钩子。

## 设置和会话

语言、字号、窗口布局、草稿偏好及每个程序集的依赖目录、搜索词和选中方法，保存在 exe 旁的 `data/settings.json`。可以从「最近打开」切换程序集，在「设置」关闭启动恢复。迁移安装目录时，若要保留记录，可以一起移动 `data`。

## 从源码构建

在 Windows 安装 .NET 10 SDK。项目默认从 NuGet 恢复固定版本的 ICSharpCode.Decompiler 和 Mono.Cecil。离线构建时可通过可选的 `IlSpyDirectory` 属性指定包含相同版本 DLL 的目录。

```powershell
dotnet build ILPatchStudio.csproj -c Release
dotnet build samples/PriceDemo/PriceDemo.csproj -c Release
pwsh -File scripts/Publish-Portable.ps1
```

示例程序集在 `samples/PriceDemo/bin/Release/net10.0/PriceDemo.dll`。PowerShell 7 发布脚本在临时目录打包，将包含示例及许可声明的自包含 ZIP 写入 `release/`，并输出 SHA-256 校验值。个人 `data` 不会进入发布包。

## 许可证

项目源码和示例均采用 [MIT](LICENSE)。第三方组件沿用各自许可，见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) 和便携包内的运行时声明。
