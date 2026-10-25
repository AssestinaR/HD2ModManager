# HD2ModManager 项目长期记忆

## 协作约束
- 仓库 E:/Data/source/repos/WpfApp1；中文通俗解释，用户负责视觉与实机验收。授权内直接做；不碰无关工作。只本地提交，不推送。
- 临时脚本放 tmp/rig-lab/，不用 _diagnostics/；不删除 .workbuddy-ai；不用 Agent 子代理，通过交流文档沟通。
- Python 用于迁移 C# 前可行性测试。用户2026-10-15澄清：源文件、测试目标槽位、资源ID等允许硬编码；将来迁入C#的数学变换必须通用，不能为三个测试模型拟合特例。详见 tmp/rig-lab/ROUND80B_SPECIAL_CASES.md。
- 外观判断权归用户；指标只定位。视觉实验交付多视图/带HD2骨架的blend/游戏包；发图前自己查看。讲参数须说明物理效果。
- 实验保留回退点；不得覆盖唯一备份。修bug做破坏关键行负对照。CLI/Core改动属另一线程，不碰不提交。
- 路线：Python验证→无Lua legacy包→用户实机验收→C#；不依赖HD2IRG1。改交付patch/gpu后跑library sync四段式。

## 环境与素材
- Python C:/Users/Administrator/.workbuddy-ai/binaries/python/envs/default/Scripts/python.exe；有numpy/scipy/Pillow，无matplotlib。
- Blender D:/SteamLibrary/steamapps/common/Blender/blender.exe；SDK docs/HD2SDK-CommunityEdition-3.8.0；游戏data D:/SteamLibrary/steamapps/common/Helldivers 2/data；原版 E:/tmp/hd2-mgroot；mod库 E:/mod。
- VPK：MourningVeil E:/tmp/mourning/MourningVeil_Zoey.vpk；Lunalice E:/tmp/pkg8/Lunalice_Zoey.vpk；Lilium E:/tmp/lilium/Lilium_Sancta_Zoey.vpk。model均survivor_teenangst；dump_blender默认Lunalice，其他必须显式--vpk。

## 坐标与关键机制
- +Y前、+Z上、+X角色右；单位米。管线烘焙网格，交付骨架保持HD2原版。
- rest_sym为pre-post；rest_hd2/rest_shipped为post后，不能直接相减。post含MESH_SHIFT=(0,-.100,-.090)与上身缩放(1.12,pivot_z=1.15,blend=.45)。点必须同空间比较。
- l_hand_twist与l_hand同为l_elbow子骨，腕处同头；前者单轴扭转，后者三自由度。搬腕枢轴要同时种两者。腕带偏心会绕小臂轴公转，肘偏心不能据此认定弯曲错误。
- 原版l_hand在小臂权重≤.013，t/L>.93才接管；腕带转角差异为设计。玩家骨架0x4d1c334d294dfa97(90骨)，动画归档18235e0c9ec0e636。
- swing/twist先翻正tw.w再取轴符号；小臂t从肘算。unit变换行主序平移末行；ti.entries[i][1]为父ti索引，bi.real_indices为slot→ti；只看LOD0引用顶点。换unit需解material remaps。

## 当前实验结论与待办
- R79 --arm-aim-wrist：d_fo_tgt=W_h-post(E_now)，用户2026-10-14验收MourningVeil正常。后续模型带此开关；不要再跑align_forearm_to_axis.py（方向轴与腕目标点冲突）。
- R80 --arm-forearm-gain：gain=目标肘腕距离−源肘腕长度；沿轴ramp，肘钉死、腕以后平移。几何门半径.12m避免牵动肘权重所属翼片。aim开启时elbow-open默认0；旧6.5°会再次偏离目标。
- R80未打包部署、未实机验收。手指rest pose未重算；rest渲染不等于游戏动画。
- Lilium真实R79基线：--gain .0814 --shift .0014 .0097 .0005 --arm-aim-wrist --elbow-open 6.5 --arm-forearm-gain off；meta.json params权威。早期默认gain=.05的对比全部作废。
- 已记录转换空间小臂长：MourningVeil288.5mm/Lunalice288.4mm/Lilium272.2mm；这些不能直接当原始源骨长度。Lilium R80a仅长度补偿，R80b肘角归零加补偿，翼会转。证据 tmp/rig-lab/out/_r80_HAND_matrix.png、_r80_cape_fullbody.png。
- 当前调查方向(2026-10-15)：先核实三个L4D2原始骨架位置与素体是否相同，再查Lilium额外补偿、Source动画修正机制。本轮用户仅授权情报收集讨论，不改代码。
- 未完成：真实游戏动作矩阵序列、原版多索引护甲解码、小臂重权进主链、shoulder_twist归零问题。RE2310护甲unit仅8槽无手臂，手臂在独立皮肤unit。

## 部署与回退
- 当前栈(rev355)：隐藏RE2310头盔→MourningVeil-RE2310-r79aim→Lunalice-AD26→Lilium-Sancta-BP20。管理器apply覆盖手动链接，持久部署改profile。
- 回退目录 E:/tmp/r79-deploy-rollback/、r76b-ship-rollback/、r76-ship-rollback/、r75b-ship-rollback/、r75-pre-align-rollback/、r74-ship-rollback/、r73-ship-rollback/、r72-ship-rollback/、round69-final-rollback/；均保留。更多历史见每日记录与_backup/MEMORY-before-r80-compaction.md。
- 贴图ID仅取basename，同底模换装会撞；新包用reid_texture.py重写并同步材质；_probe_patch_overlap.py核同栈共享ID内容，DIFF会静默覆盖。
- import复制目录，库内源同名会建_2；干净名称从库外导入。AD26/BP20头盔不受armor-normalize影响。
- library sync plan要--output，apply要--confirm；profile add/remove须GUID前缀，不支持active；deploy plan支持active。
