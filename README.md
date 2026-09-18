<div align="center">

# FModel MCP

**面向虚幻引擎游戏资产调研的 MCP 服务器** —— 挂载游戏、构建本地 SQLite 索引、用 SQL 查询、追踪包引用、导出模型与贴图动画，全部由 AI Agent 完成。基于 CUE4Parse；无需修改 FModel，无需 GUI。

<img alt="License" src="https://img.shields.io/badge/license-Apache--2.0-blue.svg">&nbsp;
<img alt=".NET" src="https://img.shields.io/badge/.NET-10-512BD4.svg">&nbsp;
<img alt="Platform" src="https://img.shields.io/badge/platform-Windows-0078D6.svg">&nbsp;
<img alt="Protocol" src="https://img.shields.io/badge/protocol-MCP-6E4AFF.svg">

[English](README_en.md) | 简体中文

</div>

## 简介

**FModel MCP** 是一个 [Model Context Protocol](https://modelcontextprotocol.io) 服务器，为 AI Agent 提供直接访问虚幻引擎游戏资产的能力。它基于 **CUE4Parse**（[FModel](https://github.com/4sval/FModel) 背后的资产解析库）挂载游戏的 `.pak` 与 IoStore 容器，为每个 package 与 export（模型、贴图、动画、音频、材质等）建立本地 SQLite 索引，并通过十个工具覆盖完整的调研链路：结构发现、只读 SQL 查询、包级引用图、资产 JSON 检视与多格式导出（UEFormat、glTF、ActorX、USD、PNG、原始字节）。无需 GUI，也无需对 FModel 或 CUE4Parse 做任何修改。服务器按 agent-first 理念设计：空结果必附原因、数值指标通过合理性校验、数据集完整性告警先于不可靠数据到达调用方。已在两款商业游戏上完成端到端验证，包括 70 万+ 文件、220 万+ export 记录规模的索引。

## 特性

- **10 个 MCP 工具** — `open_game`、`index_status`、`describe_dataset`、`list_dirs`、`query_index`、`find_references`、`expand_references`、`index_paths`、`read_asset`、`export_assets`
- **索引可伸缩** — 单个角色目录秒级、全量游戏分钟级（并行扫描 + 单写者 SQLite）
- **只读 SQL 查询索引** — 面向 LLM 驱动分析，无需学习任何查询 DSL
- **包级引用图** — 正向边随取随用，反向图在索引期构建
- **多格式导出** — UEFormat（`.uemodel`/`.ueanim`）、glTF、ActorX（`.psk`/`.psa`）、USD、贴图、JSON 属性、原始字节
- **广泛游戏覆盖** — UE4/UE5、pak 与 IoStore 容器、AES 加密归档、`.usmap`/`.jmap` 映射、游戏特有怪癖处理
- **零补丁设计** — 与现有 FModel 工作区并存；只读复用其原生库与配置

## 仓库结构

```
FModel.Mcp/        MCP 服务器（.NET 10，stdio 传输）
  Tools/           MCP 工具实现
  Indexing/        并行扫描、指标提取、SQLite 结构、合理性校验
  Session/         游戏会话、索引复用与失效逻辑
  Runtime/         原生库加载、FModel 配置发现、导出路径安全
probes/            独立验证工具（见 probes/README.md）
```

## 环境要求

- **Windows**（原生库处理目前面向 Windows 版 FModel）
- **.NET 10 SDK**
- 本地 **FModel 检出（含 `CUE4Parse` 子模块）** —— 构建期以源码方式引用

## 构建

```powershell
git clone https://github.com/noobdawn/fmodel-mcp.git
cd fmodel-mcp

# 在本仓库旁克隆 FModel（构建期提供 CUE4Parse 源码，运行期提供原生库）
git clone --recursive https://github.com/4sval/FModel.git ..\FModel

# 构建与发布
dotnet publish FModel.Mcp/FModel.Mcp.csproj -c Release -o FModel.Mcp/publish -p:CUE4PARSE_SKIP_NATIVE=true
```

说明：

- `CUE4PARSE_SKIP_NATIVE=true` 跳过 CUE4Parse 原生组件构建（否则需要 CMake）。
- 用 `-p:CUE4ParseRoot=D:\path\to\CUE4Parse` 指向其他位置的 CUE4Parse 检出。
- **原生库**（Oodle、zlib-ng、Detex、ACL）不在本项目中分发。运行时会自动从本地 FModel 安装（经 `AppSettings.json` 定位其 `<OutputDirectory>\.data\` 目录）、FModel 单文件解包目录或本程序所在目录查找。

## 配置 MCP 客户端

```json
{
  "mcp": {
    "fmodel": {
      "type": "local",
      "command": ["<仓库路径>\\FModel.Mcp\\publish\\fmodel-mcp.exe"],
      "environment": {
        "FMODEL_MCP_INDEX_DIR": "D:\\FModelMcpIndex"
      }
    }
  }
}
```

- `FMODEL_MCP_INDEX_DIR` 可选 —— 默认索引位于 `%LocalAppData%\FModelMcp\index`。
- 游戏路径、UE 版本预设、AES 密钥与 `.usmap`/`.jmap` 路径均在调用时作为 `open_game` 参数传入；代码中无任何硬编码。

## 工具清单

| 工具 | 用途 |
|---|---|
| `open_game` | 挂载游戏目录并启动后台索引（可限定范围） |
| `index_status` | 索引进度、数据集完整性、IoStore 诊断 |
| `describe_dataset` | 资产组织概览 + 索引 SQL schema |
| `list_dirs` | 目录树浏览器（支持模式过滤与多键排序） |
| `query_index` | 对资产索引执行只读 SQL（单条 `SELECT`/`WITH`） |
| `find_references` | 单包引用（实时正向 / 索引反向） |
| `expand_references` | 多起点批量引用展开与类型聚合 |
| `index_paths` | 增量索引指定包（突破索引范围限制） |
| `read_asset` | 将任意资产反序列化为 JSON（分页 / 截断） |
| `export_assets` | 导出模型、贴图、动画与材质为文件 |

## 探针（Probes）

用于在没有 IDE 宿主的场景下验证行为的独立小工具：

- **`probes/AclProbe/`** —— ACL 压缩动画解析的 A/B 探针；验证 `/ACLPlugin` 虚拟路径修复（`CompressedDataStructure` 由 `NULL` → 成功解析，可选真实导出）
- **`probes/mcp_client.py`** —— 最小 MCP stdio 客户端；直接驱动服务器（`verify` / `count` / `chunk` / `raw` 任务）

两者均通过命令行参数或环境变量接收连接信息。**本仓库不存储任何密钥或游戏路径。**

## 已知限制

- 反向引用图仅支持传统 `.pak` 归档（IoStore `.utoc/.ucas` 包可挂载与索引，但不产生引用边）
- 无 3D 预览（模型可导出为 `.glb` 供外部查看器查看）
- 行为取决于 CUE4Parse 对各游戏的支持程度；目前验证过的游戏数量有限

## 许可证与致谢

[Apache-2.0](LICENSE) © 2026 noobdawn

本项目不包含任何游戏内容，且从不再分发专有原生库；原生库在运行时从本地 FModel 安装加载。基于 CUE4Parse（Apache-2.0）。与 FModel 及 Epic Games 无关联。
