# Recorder 集成补丁

上游仍固定为 DLSS5Tool v2.3.3 / e53e0b4d5139126e7b31866ed6b3653bbba0f449，未升级原生 DLL 或更改推理算法。

2026-09-28，RTX 5080 集成测试：

- `super_resolution.py`、`dlss_host_process.py`：仅当桥接设置 `AA_RECORDER_DLSS_WORK` 时，将本次任务的 VSR/神经渲染原生日志写到已创建的 `dlss-diagnostics`，关闭后保留。上游原本会删除临时日志，无法对成功推理留证。不修改便携工具及其用户状态；独立上游默认行为不变。
- `frame_generation.py`：报告实际初始化的 VSR、神经渲染后端/适配器，以及已核对一致的 DLSSG/NVOFA LUID。记录初始化不单独等同于整段推理成功，仍需完成状态、帧计数及视频验收。

桥接自身的 Windows 进度文件占用修复见 `bridge/enhance.py` 与 `tests/test_progress_publish.py`。
