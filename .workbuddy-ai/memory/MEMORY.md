# HD2ModManager 项目记忆

## 协作与工作约定
- 仓库：`E:/Data/source/repos/WpfApp1`。用户非程序员，中文解释通俗；用户负责游戏/视觉验收，助手做代码与数值核查。
- 项目临时脚本和交接放 `tmp/rig-lab/`；重要证据候选分开保存，禁止覆盖唯一回退点。`tmp/`暂保留；不删 `.workbuddy-ai`。只本地提交、不推送。
- 小臂/网格故障必须看整体形状、做单项消融和破坏关键行负对照；漂亮的局部标量不等于形状正确。发图前先肉眼检查。
- 迭代路线：Python 离线探针/patch → 用户实机验收 → 通过后再迁 C#。无 Lua legacy 必须能运行。交付目录 patch/gpu 有改动时必须执行 `library sync` 四段流程。

## 环境与路径
- Python：`C:/Users/Administrator/.workbuddy-ai/binaries/python/envs/default/Scripts/python.exe`（numpy/Pillow/scipy/lz4）。Blender：`D:/SteamLibrary/steamapps/common/Blender/blender.exe`。
- SDK：`docs/HD2SDK-CommunityEdition-3.8.0`；游戏 data：`D:/SteamLibrary/steamapps/common/Helldivers 2/data`；原版抽取：`E:/tmp/hd2-mgroot/`；mod库：`E:/mod/`。
- Source 模型路径默认 Lunalice；使用 `E:/tmp/lilium/` 或 `E:/tmp/mourning/` 必须显式指定源目录参数。

## 当前项目状态
- 交付和论证详细记录在 `tmp/rig-lab/ROUND*.md` 与 `.workbuddy-ai/memory/YYYY-MM-DD.md`；这里仅保留决策。用户接受了模型摆姿、骨架、显示和阴影；当前重点是小臂动作手感与材质呈现。
- 第60轮确认旧材质贴图表写入错误：贴图槽键和贴图 ID 必须分别连续写入；已回填 `ROUND60_ISSUES_BRIEF.md`。小臂既有源权重在肘腕间分布过宽，需局部调整；禁止借权重步骤重摆模型。
- 第69轮小臂方案：`smoothstep(0.22L,1.00L)` 全部给 `l_hand`，相较旧版实机明显改善。第72轮交接 `tmp/rig-lab/ROUND72_FOREARM_BRIEF.md` 发现残余橡皮筋感：小臂远端自由度多，过渡影响形状；Lilium/MourningVeil 的手骨近端权重渗入已低至0.012/0.029，但离线30°腕子树旋转中位位移仍13.8/17.6mm。小臂手指骨权重为0；问题不是归一化或骨索引错误。
- 不可直接采纳“复制原版 hand_twist 权重”或单用窄带：第70轮互斥分配实验令拧转更差；窄过渡带曾有大边横跨，导致撕裂。须拆分手骨动作与twist动作验证，并在真实模型上按边长约束测试。
- 第72轮待裁定：原版有 `l_hand_twist` 权重，但其动画矩阵行为未知；不能由“原版用了”推出当前护甲需照搬。第6步重权应成为明确管线步骤，不能靠手动补跑。
- 头盔是独立 `Helmet` archive，躯干护甲归 `Armor`；归一化隐藏只按ArchiveId作用。交付物需同步库。

## 稳定技术约定
- HD2 +X右 +Y前 +Z上，单位米；骨架不改，只变网格和运行时权重。半精度权重应合并同骨后编码并验证权重和、骨索引及反导入。
- 同键重建 `SameKeyCanonicalUnitRebuilder` 用原生Unit作为ABI/坐标框架；不能仅凭自写编解码互相通过证明格式正确。原版网格坐标变换、顶点格式、LOD及边界都要独立核验。
- 材质ID根据 `$basetexture` 全路径；底色贴图ID按basename，因此不同包同basename内容不同会冲突，同一皮肤多材质需共享basename/id。
