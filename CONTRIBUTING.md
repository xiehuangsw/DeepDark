# 参与开发

感谢有兴趣参与！这个项目很小，规则也很简单。

## 反馈问题 / 提建议

直接用 [Issues](https://github.com/xiehuangsw/DeepDark/issues)：
- 问题反馈请选 "问题反馈" 模板，把版本、Windows 版本、是否开 HDR 填上
- 功能建议请说明**想解决的问题**，而不只是想要的功能

## 修改代码

1. Fork 本仓库并 clone 到本地
2. 改 `src/DeepDark.cs`（整个程序就这一个文件）
3. 编译验证：

   ```powershell
   powershell -ExecutionPolicy Bypass -File build.ps1
   # 产物：dist\DeepDark.exe
   ```

4. 提交 PR，说明改动目的与验证方式（例如："在 150% 缩放下不再裁切，附截图"）

## 代码约定

- **零第三方依赖**：只用 .NET Framework 自带的 WinForms / GDI / Win32 API，
  编译只依赖 Windows 自带的 `csc.exe`
- **兼容性**：只用 Vista 以来就存在的 API，保持 Windows 10 / 11 都能跑
- **开销**：这是常驻后台的小工具，避免引入周期性分配、额外线程或轮询；
  显示器 / 电源变化请走窗口消息事件驱动
- **注释**：中文注释，解释"为什么这么做"（尤其是踩过的坑），而不只是"做了什么"

## 验证建议

改完请至少确认：

- `DeepDark.exe --level=40` 时读回显示器 LUT 比例约为 0.60，`--restore` 后回到 1.000
- 退出程序后屏幕完全还原（gamma 回到 1.0），任务管理器里没有残留进程
- 内存 / CPU 没有明显上涨（任务管理器看"内存"列，正常约 5~10 MB）
