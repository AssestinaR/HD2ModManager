# 鎶ょ敳瀹炰緥 bank锛?*浠ｇ爜绾ц瘉鎹?*锛?026-10-17锛?
> 鏈枃鎶?`armor-piece-capacity-observations-20261016.md` 閲岄偅鏉?鍙楠岀嚎绱?
> 锛坄bank = 0 if type==0 else 1 if type==1 else 2`锛夊崌绾ф垚**浠ｇ爜浜嬪疄**锛?> 骞惰褰曚竴娆℃椿杩涚▼璇诲彇鐨勫熀绾裤€?*浠嶆湭**瑙ｉ噴 21/22 閭ｆ潯缁忛獙杈圭晫锛堣 搂6锛夈€?
## 1. 涓轰粈涔堣鎶撳唴瀛橀暅鍍?
鐩樹笂鐨?`data/game/game.dll` 鍙湁 15,522,408 B锛?xECDA68锛夛紝鑰屽畠 PE 澶撮噷鐨?`SizeOfImage` 鏄?74,727,424 B锛?x4744000锛夆€斺€?鍜岃繍琛屾椂 `runtime.status` 鐨?`game_image_size` 涓€鑷淬€傜洏涓婇偅浠芥槸澹筹紝**RVA 鍙鍔犺浇鍚庣殑鍐呭瓨鏈夋剰涔?*銆?
`scripts/capture_game_image.py` 鍙鍦帮紙`PROCESS_QUERY_INFORMATION|PROCESS_VM_READ`锛?涓嶅啓杩涚▼銆佷笉鎸傜嚎绋嬨€佷笉鏀逛繚鎶ゅ睘鎬э級鎶婃暣涓ā鍧楄鍑烘潵锛岃惤鎴?*闀滃儚甯冨眬**锛?鏂囦欢鍋忕Щ鎭掔瓑浜?RVA锛屾湭鎻愪氦鐨勯〉鐣?0銆傝繖娆℃姄鍙?23 涓尯闂村叏閮ㄥ彲璇伙紝0 澶辫触锛岃€楁椂 0.4 s銆?
```
python HD2RuntimeArmorOverride/scripts/capture_game_image.py \
  --pid <pid> --module game.dll \
  --output E:\tmp\hd2-mem\game.image --meta E:\tmp\hd2-mem\game.image.meta.json
```

## 2. 涓変釜 bank 鐨勫鍣ㄥ舰鐘讹紙**缁撹**锛?
### 2.1 姣忎釜 bank 鏄?`uint32[10]`

`0x81e010` 鏄竴涓竻绌哄嚱鏁帮紝`rcx` = 鏁扮粍鍩哄潃锛宍edx` = 璁板綍搴忓彿锛?
```
0081e02c: mov esi, 0xa                 ; 10 涓Ы
0081e031: imul rbp, rax, 0x1b8         ; rec = base + index * 0x1b8
0081e03a: add rbp, rcx
0081e040: lea rbx, [rbp + 0x546b4c]    ; bank A
0081e047: mov ecx, [rbx]               ; 妲?0 璧?0081e0a1: add rbx, 4
0081e0a5: sub rdi, 1
0081e0a9: jne 0x81e047                 ; 瑕嗙洊 +0x00 .. +0x24锛?0 涓?dword锛?```

鍚屼竴褰㈢姸鍦ㄥ彟澶栦笁澶勭嫭绔嬩唬鐮侀噷閫愬瓧閲嶅锛坄mov e??, 0xa` + `imul r*, rax, 0x1b8`锛夛細
`0x794fd9`銆乣0x796489`銆乣0x81e02c`銆傛墍浠ワ細

* 涓変釜 bank **杩炵画**鏀惧湪鍚屼竴涓?0x1b8 姝ラ暱鐨勮褰曢噷锛岄棿闅?**0x28 = 40 B**锛?
  | bank | 鍋忕Щ | 鎵胯浇 |
  |---|---|---|
  | A | `rec + 0x546b4c` | `piece_type == 0` |
  | B | `rec + 0x546b74` | `piece_type == 1` |
  | C | `rec + 0x546b9c` | `piece_type >= 2` |

* 绱ц窡鍦?C 涔嬪悗鏄?`+0x546bc4` / `+0x546bc8` 涓や釜 dword锛屽嵆 `0x904ac1` 浼犺繘
  鐢熶骇鑰呬綔涓?妲?3 / 妲?2 涓撶敤鍊?鐨勯偅涓や釜鍙傛暟銆?
### 2.2 绱㈠紩椤哄簭鏄?**`bank[slot]`**锛屼笉鏄?`slot[bank]`

鐢熶骇鑰?`0x81f470` 鐨勬敹灏撅紙`0x8201f5` 灏辨槸 note 閲岄偅涓?bank 閫夋嫨鐐癸級锛?
```
008201f5: mov ecx, dword ptr [r12 + 0xc]    ; piece_type
008201fa: mov eax, dword ptr [r12 + 8]      ; slot
008201ff: test ecx, ecx
00820201: jne 0x82020a
00820203: mov rcx, [rsp + 0x58]             ; bank A
00820208: jmp 0x820253
0082020a: cmp ecx, 1
0082020d: jne 0x82024e
0082020f: mov rcx, [rsp + 0x60]             ; bank B
00820214: mov dword ptr [rcx + rax*4], edi  ; bankB[slot] = handle
0082024e: mov rcx, [rsp + 0x68]             ; bank C
00820253: mov dword ptr [rcx + rax*4], edi  ; bank[slot] = handle
```

杩欏悓鏃?*鐙珛纭浜?Piece 鐨?ABI**锛歚+8 = slot`銆乣+0xc = piece_type`銆?涓変釜 bank 鐨勫熀鍧€鍒嗗埆鏄皟鐢ㄨ€呯殑绗?8/9/10 涓爤鍙傛暟锛岀敱 `0x877c70..0x877cf9` 浼犲叆銆?
### 2.3 鎺ㄨ锛氬悓 `(type, slot)` 鍙湁鏈€鍚庡啓鍏ヨ€呭瓨娲?
鍐欏叆鏄８鐨?`bank[slot] = handle`锛屾病鏈?宸插崰鐢?妫€鏌ャ€傛墍浠ヤ袱涓?Piece 鍙
`(piece_type, slot)` 鐩稿悓锛屽墠涓€涓殑鍙ユ焺灏辫瑕嗙洊 鈥斺€?杩欏氨鏄?12 缁勯厤缃噷
"鍒悕缁?/ 琚帇浣忕殑 Piece 鏁?鐨勬満鍒讹紝鐜板湪鏄?*浠ｇ爜璇佹槑**鑰岄潪鎺ㄦ祴銆?
**妲戒綅涓嶄細瓒婄晫**锛歚slot` 瑙傛祴鍊煎煙鏄?0..9锛宐ank 鎭板ソ 10 涓?dword锛?`bank + slot*4` 鏈€杩滃埌 `+0x24`锛屼粛鍦?40 瀛楄妭鍐呫€備箣鍓嶆媴蹇冪殑"妲?9 韪╁埌涓嬩竴涓?bank"
涓嶆垚绔嬨€傛Ы 8/9 鏄?MaterialLut/PatternLut锛屾湰鏉ュ氨璇ョ敤婊°€?
### 2.4 涓€澶勭湡瀹炵殑涓嶅绉帮紙涓嶆槸 21/22 鐨勫師鍥狅紝浣嗗€煎緱璁帮級

* 鐢熶骇鑰?`0x81f470` 鐨勬竻鍦哄惊鐜細`r14d = 9`銆侀鍋忕Щ `+4` 鈫?鍙竻 **妲?1..9**銆?* 娓呯┖鍑芥暟 `0x81e010`锛歚esi = 0xa`銆侀鍋忕Щ `+0` 鈫?娓?**妲?0..9**銆?
涔熷氨鏄姣忓抚閲嶅缓鏃?*妲?0 鐨勬棫鍙ユ焺涓嶄細琚敓浜ц€呮竻鎺?*锛屽彧鏈夋暣浣撻攢姣佹椂鎵嶆竻銆?
## 3. Piece 鐨勫瓨鍌ㄦ槸**鍔ㄦ€佹暟缁?*锛屼笉鏄畾闀?
`0x81f470` 涓庢秷璐硅€?`0x81ed60` 閮界敤鍚屼竴濂楄娉曪細

```
count   = dword [kit + 0x38]          ; 缁勬暟
groups  = qword [kit + 0x30]          ; 姣忕粍 24 瀛楄妭
group_i = { dword 閫夋嫨瀛? qword Piece鏁扮粍; dword Piece鏁?}   ; +0 / +8 / +0x10
Piece   = group.Piece鏁扮粍 + j * 0x60  ; 0x60 = 96 = Piece 璁板綍澶у皬
```

**娌℃湁浠讳綍鎸?`piece_type` 绱㈠紩鐨勫畾闀挎暟缁勩€?* 瀹氶暱鐨勪笢瑗垮彧鏈変袱鏍凤細
涓変釜 10 妲?bank锛埪?锛夛紝鍜屼笅闈㈣繖涓や釜 20 椤规寚閽堟暟缁勩€?
## 4. 鍞竴鎵惧埌鐨勫畾闀?Piece 瀹瑰櫒锛?*20 椤癸紝涓旈潤榛樻埅鏂?*

`0x81f470` 鍦?`[rbp-0x10]`锛?0 椤癸級閲屾敹闆?灞炰簬褰撳墠 body 鍙樹綋鐨?Piece 鎸囬拡"锛?
```
0081f709: mov edi, dword ptr [rbx + 0x38]   ; 缁勬暟
0081f720: cmp r12d, 0x14                    ; <- 20
0081f724: jae 0x81f77a                      ; 鍒?20 灏辩洿鎺ヨ烦鍑哄幓
0081f72e: mov eax, dword ptr [r9 + r8*8]    ; group.閫夋嫨瀛?0081f732: cmp eax, r14d
0081f735: je 0x81f73c
0081f737: cmp eax, 3                        ; 鎴?== 3锛堥€氶厤锛?0081f73a: jne 0x81f772
0081f74e: lea rcx, [rdx + rdx*2]
0081f755: shl rcx, 5                        ; j * 0x60
0081f763: mov qword ptr [rbp + rax*8 - 0x10], rcx   ; 鏀堕泦
```

娑堣垂鑰?`0x81ed60` 鍦?`[rsp+0x70]` 閲屽仛**瀹屽叏鐩稿悓**鐨勪簨锛屽悓鏍?`cmp edx, 0x14; jae`
锛坄0x81ee10` / `0x81ee37`锛夈€?
鎵€浠ワ細**涓€涓?Kit 鍦ㄦ煇涓?body 鍙樹綋涓嬫渶澶氬彧鏈?20 涓?Piece 琚繖涓€瀵瑰嚱鏁板鐞嗭紝
绗?21 涓捣琚潤榛樹涪寮冿紝涓嶆姤閿欍€?*

## 5. 娲昏繘绋嬪熀绾匡紙娓告垙鍦ㄨ窇锛宍active_mappings=0`锛?
鍏ㄥ眬鍦?`game.dll + 0x3326d20`锛坄0x796497: add r14, [rip+0x2b90882]`锛夛細

```
*(game.dll + 0x3326d20) = 0x194a7dcabd8        ; 鏁扮粍鍩哄潃
rec(k) = 鍩哄潃 + k * 0x1b8
```

`scripts/peek_armor_banks.py --pid <pid> --global-rva 0x3326d20 --bulk 4096` 鑳芥壂鍒?4096 鏉¤褰曘€佸叾涓害 1600鈥?800 鏉″湪璇ュ亸绉讳笂鏈夐潪闆舵暟鎹€?
**浣?`rec[0]` 涓嶆槸鎶ょ敳瀹炰緥 鈥斺€?杩欐槸涓€涓凡鎺ㄧ炕鐨勫亣璁俱€?* `rec[0]` 纭疄寰堝儚
锛?6 涓敮涓€銆佽繛鍙风殑灏忔暣鏁?`0x874..0x883`锛屽垎甯?9脳type0 + 6脳type1 + 1脳type鈮?锛夛紝
鍙槸鍒囨崲 Piece 閰嶇疆鍚庡畠**閫愬瓧鑺備笉鍙?*銆傚垽鎹笉鑳介潬"鐪嬭捣鏉ュ儚鍙ユ焺"锛屽彧鑳介潬璺ㄩ厤缃樊鍒嗐€?
鍚屾牱鍦帮紝`+0x546b4c` 杩欐鍦板潃绌洪棿鏄?*澶氱被鍨嬪璞″叡浜?*鐨勶細澶ч噺璁板綍閲屾槸娴偣
锛坄440d323c` 鈮?565.5銆乣3f800000` = 1.0锛夋垨鎸囬拡锛屽彧鏈変竴閮ㄥ垎鏄彞鏌勩€?
### 5.1 涓€娆?鍗忚鎯版€?鐨勫疄娴嬶紙閬垮厤鎶婂櫔澹板綋淇″彿锛?
2026-10-17 瀹炴満锛氬啗姊板簱閫変腑 RE-2310"鑽ｈ獕鍗槦"锛屽睆骞曚笂鏄剧ず鐨勬槸**宸插畨瑁呯殑
RE-2310 鍩虹鍖?*锛堜笉鏄崗璁槧灏勶級銆傛鏃跺崗璁噷 `map_model` 涓?**0 鏉?*锛?鎵€浠?`use_armor_piece_config.py --config S8 / Y` 鍙敼浜?`model_piece`锛?**娌℃湁钀藉埌浠讳綍鎶ょ敳涓?*銆備笁浠借瘉鎹簰鐩稿嵃璇侊細

| 璇佹嵁 | S8锛?1 浠?/ 15 鐢熸晥锛?| Y锛?8 浠?/ 22 鐢熸晥锛?| 缁撹 |
|---|---|---|---|
| 璁板綍闆嗗悎澶у皬 | 1643 鏉￠潪闆?| 1643 鏉￠潪闆?| 缁撴瀯鏈彉 |
| 宸垎 | 鈥?| 93 鏉″彉鍖栵紝鍏ㄩ儴鏄诞鐐瑰姩鐢诲€?| 鏃犵粨鏋勬€у彉鍖?|
| 绐楀彛鎴浘 | `armory-S8.png` | `armory-Y.png` | 鍙樊闀滃ご杩滆繎锛屾ā鍨嬩竴鑷?|

**鏁欒锛氫互鍚庢瘡娆″疄楠屽繀椤诲厛纭 `map_model` 鏉℃暟 > 0锛屽惁鍒欓噺鍒扮殑鏄櫔澹般€?*

### 5.2 椤哄甫浜ゅ弶楠岃瘉浜?RE-2310 鍩虹鍖?
鍚屼竴寮犳埅鍥撅紙`armory-S8.png`锛夋樉绀猴細鍐涙搴撻€変腑 RE-2310"鑽ｈ獕鍗槦"锛?3D 棰勮鏄?*鑷畾涔夋ā鍨嬬殑瑁镐綋 + 闀垮彂**锛屾病鏈変换浣曟湇瑁?閰嶄欢锛?鍒楄〃缂╃暐鍥鹃噷杩樿兘鐪嬪埌鍘熺増鎶偐锛坰lot 1锛夈€?
杩欎笌浜や粯鍖呯殑鏋勯€犲畬鍏ㄥ寰椾笂锛?3 涓?Piece 閲?11 涓崲鎴愯嚜瀹氫箟妯″瀷鐨?7 涓韩浣撻儴浠躲€?12 涓崲鎴愰殣钘忓崰浣嶇锛屾墍浠ュ睆骞曚笂**鍙墿韬綋**銆備篃灏辨槸璇?"鐪嬭捣鏉ユ病绌胯。鏈?鏄?`E:\tmp\lunalice-shop` 閲?**A1 涓讳綋**锛堣８浣撴湰浣擄級鐨勬甯歌〃鐜帮紝
涓嶆槸浜や粯缂洪櫡锛涙湇瑁呮潵鑷?B/C/D/E/F 閭ｄ簺鐙珛 VPK銆?
杩欐潯鍚屾椂璇存槑锛歚capture_window.py` 鐨勬埅鍥惧彲浠ヤ綔涓?*鐙珛浜庡唴瀛樿鍙?*鐨勭浜岃瘉鎹簮锛?鍚庨潰鍒ゆ柇"鏄病娓叉煋杩樻槸琚崲鍥炲師鐗?鏃惰鐢ㄥ畠銆?
## 6. **21/22 宸茶В閲?*锛?0 椤规埅鏂?+ 銆孉ny 缁勫湪鍓嶃€?
### 6.1 瀹氫綅鍒版姢鐢插疄渚嬭褰?
娓叉煋璺緞 `0x877b79: mov r13, [rip + 0x2aaf1a0]` 鈫?鍏ㄥ眬 **`game.dll + 0x3326d20`**锛?鏁扮粍鍩哄潃 = `*(game.dll + 0x3326d20)`锛宍rec(k) = 鍩哄潃 + k*0x1b8`锛?*鎶ょ敳瀹炰緥鏄?`rec[0]`**銆?
鍒ゆ嵁鏄?*璺ㄩ厤缃樊鍒?*锛堜笉鏄?鐪嬭捣鏉ュ儚鍙ユ焺"锛夛細琛ヤ笂
`map_model=820333399,custom-eaf880703fa94f398d620d2288b25d1f` 鍚庯紝
`rec[0]` 绔嬪埢浠?16 涓棫鍙ユ焺鍙樻垚 9 涓柊鍙ユ焺 `0x400a62..0x400a74`銆?
### 6.2 bank A 鐨勬Ы闆嗗悎鐩存帴鍐冲畾"鍝簺韬綋閮ㄤ欢鍦?

`rec[0]` 鐨?bank A 鎵胯浇 `piece_type == 0`锛屼篃灏辨槸**韬綋鏈綋**銆?瀹炴祴锛坄active_mappings=1`锛岄€愮骇鍒囬厤缃級锛?
| 閰嶇疆 | 鎬?Piece | 鐢熸晥 | bank A 闈為浂妲?| 鐢婚潰 |
|---|---|---|---|---|
| S4 | 17 | 11 | 0,2,3,4,5,6,7 | 韬綋瀹屽ソ |
| S8 | 21 | 15 | **0,2,3,4,5,6,7** | 韬綋瀹屽ソ |
| K1 | 16 | 10 | 0,2,3,4,5,6,7 | 韬綋瀹屽ソ |
| K4 | 25 | 19 | 0,2,3,4,5,6,7 | 韬綋瀹屽ソ |
| X6 | 27 | 21 | **0,2,3,4,5,6,7** | 韬綋瀹屽ソ |
| Y | 28 | 22 | 0,2,3,4,5,6,7锛圔/C 鍙樻繁锛?| 鍙岃噦娑堝け |
| M3 | 28 | 22 | 0,2,3,4,5,6,7 | 鍙宠噦娑堝け |
| M4 | 32 | 26 | 缂╂按 | 宸﹁噦/鑳?鍙岃吙娑堝け |
| M5 | 35 | 29 | 缁х画缂╂按 | 鍐嶆帀鑳?|
| **C3** | **43** | **37** | **鍙墿 0** | **鍙墿澶?+ 瑁欏瓙** |

**缁撹锛氶儴浠舵秷澶卞彂鐢熷湪 bank 鍐欏叆涔嬪墠**锛屼笉鏄覆鏌撻樁娈电殑闂銆?
### 6.3 涓轰粈涔堝亸鍋忔槸韬綋浠惰涓?
C3 鐨勯€愭Ы娓呭崟閲岋紝slot 2..7 **姣忎釜閮芥湁 type0 韬綋浠?*锛圕hest/Hips/LeftLeg/
RightLeg/LeftArm/RightArm 鍚?body0 + body1锛夈€傚畠浠湪 Kit 閲岋紝鍗翠竴涓兘娌¤繘 bank A銆?
鍘熷洜鏄?搂4 閭ｄ釜 20 椤规暟缁勭殑**鏀堕泦椤哄簭**銆俙0x81f470` 鎸?Kit 鐨勭粍鏁扮粍椤哄簭閬嶅巻锛?缁勭殑鍏ラ€夋潯浠舵槸 `group.閫夋嫨瀛?== 褰撳墠body鍙樹綋 || == 3`锛? = "Any"锛屼笌 body 鏃犲叧锛夛細

```
0081f72e: mov eax, dword ptr [r9 + r8*8]    ; group.閫夋嫨瀛?0081f732: cmp eax, r14d
0081f735: je 0x81f73c
0081f737: cmp eax, 3                        ; "Any" 缁勬案杩滃叆閫?0081f73a: jne 0x81f772
```

C3 閲?"Any" 缁勮嚜韬氨鏈?**31 涓?* Piece锛?head 5 + chest 6 + hips 3 + leftleg 8 + rightleg 5 + leftarm 4 = 31銆?
**31 > 20**锛屾墍浠ュ墠 20 涓悕棰濆叏琚?Any 缁勫悆鎺夛紝body 涓撳睘鐨?type0 韬綋浠?涓€涓兘娌¤疆鍒?鈥斺€?浜庢槸 bank A 鍙墿 head锛坔ead 灞炰簬 Any 缁勪笖鎺掑湪鏈€鍓嶏級銆?
S8锛?1 涓€丄ny 缁勫皬锛夋椂 Any 缁?+ body 浠堕兘鍦?20 浠ュ唴锛屼竷涓韩浣撴Ы榻愬叏锛?鎵€浠ヨ韩浣撳畬濂姐€?*"鎬绘暟鍐冲畾鎺夊灏戙€佸垎甯冨喅瀹氭帀鍝釜"鍦ㄨ繖閲屽緱鍒板畬鏁磋В閲婏細
鎬绘暟鍐冲畾 Any 缁勫崰鎺夊灏戝悕棰濓紝鍒嗗竷鍐冲畾鍓╀笅鍚嶉杩樺涓嶅缁?body 浠躲€?*

### 6.4 杈圭晫涓轰粈涔堢湅璧锋潵鏄?21/22 鑰屼笉鏄?20

鏁扮粍瀹归噺鏄?20锛坄cmp ..., 0x14; jae`锛夛紝鎵€浠?*绗?21 涓捣**鎵嶅紑濮嬩涪銆?鐢熸晥 21 鏃跺彧涓?1 涓紝鑰屽疄娴嬮偅鍑犱釜閰嶇疆閲岃涓㈢殑鎭板ソ鏄啑浣欎欢锛堝悓 `(type,slot)`
鏈潵灏变細琚埆鍚嶅帇鎺夛級锛屾墍浠?*鐪嬭捣鏉?* 鈮?1 瀹屽ソ锛涘埌 22 涓?2 涓紝鎵嶅紑濮嬩涪鍒扮湡閮ㄤ欢銆?
### 6.5 鍙搷浣滅粨璁?
* 姣忎釜 (Kit, body 鍙樹綋) 鐨?*鐢熸晥 Piece 蹇呴』 鈮?20**锛屽惁鍒欒秴鍑洪儴鍒嗛潤榛樺け鍘诲疄渚嬨€?* 鏇寸簿纭湴璇达細**"Any" 缁勭殑 Piece 鏁板繀椤荤暀鍑鸿冻澶熷悕棰濈粰 body 涓撳睘浠?*銆?  缁勫悎鏃舵妸 body 鏃犲叧鐨勮楗颁欢鏁伴噺鍘嬩笅鏉ワ紝鎴栬€呰韬綋浠舵帓鍦ㄥ墠闈€?* 杩欎竴鏉″悓鏃惰В閲婁簡涓轰粈涔?姣忔Ы鍑犱釜"涓嶆槸瑙﹀彂鐐癸紙S8 鍦?slot 4 濉炰簡 9 涓篃娌′簨锛夛細
  瑙﹀彂鐐规槸**鏀堕泦鎬绘暟**锛屼笉鏄崟妲芥繁搴︺€?
### 6.6 浠嶆湭瑙ｉ噴

* 涓轰粈涔?Y锛?2 鐢熸晥锛夋帀鐨勬槸鍙岃噦銆丮3锛?2 鐢熸晥锛夊彧鎺夊彸鑷?鈥斺€?鐜板湪鍙互褰掑洜涓?  "琚尋鍑哄墠 20 鐨勫叿浣撴槸鍝簺 Piece"锛屼絾**閫愰厤缃娴?*杩樻病鍋氾紙闇€瑕佺煡閬撹繍琛屾椂
  鍐欒繘 Kit 鐨勭粍椤哄簭锛夈€?* 鎺夌殑鏄?娌℃覆鏌?杩樻槸"琚崲鎴愬師鐗?锛歜ank 閲屾病鏈夊彞鏌?鈬?娌℃湁瀹炰緥 鈬?鏄?娌℃覆鏌?锛?  杩欎竴鏉＄幇鍦ㄥ彲浠ョ‘璁や簡銆?
## 9. **鏈壘鍒?bank 鐨勬覆鏌撴秷璐硅€?*锛?026-10-17锛岄噸瑕佸惁璇侊級

鍏ㄩ暅鍍忔壂浜嗕袱閬嶏細

* `mov [base + idx*4], reg`锛堝啓鍏ワ級**1457 澶?*锛屾姢鐢插瓙绯荤粺閲屽彧鏈?  `0x820214` 鍜?`0x820253` 涓ゅ 鈥斺€?閮藉湪鐢熶骇鑰?`0x81f470` 閲屻€?* `mov reg, [base + idx*4]`锛堣鍙栵級**1677 澶?*锛屾寜鍦板潃鍖洪棿杩囨护鍚庢病鏈変竴澶?  钀藉湪寮曠敤 bank 鍋忕Щ锛坄0x546b4c/74/9c`锛夌殑鍑芥暟閲屻€?
鎵€鏈夊紩鐢ㄩ偅涓変釜鍋忕Щ鐨勪唬鐮佸彧鏈夎繖鍑犵被锛?
| 鍦板潃 | 鎬ц川 |
|---|---|
| `0x81f470` 鍐呯殑 `0x820445` / `0x823609` / `0x823652` | 鐢熶骇鑰呮竻鍦哄惊鐜?|
| `0x81e010` / `0x81e040` | 閿€姣侊紙10 妲?脳 3 bank锛?|
| `0x794fec` / `0x79649e` | 閿€姣?|
| `0x877c70` | 璋冪敤鑰咃紝鎶婁笁涓寚閽堜紶缁欑敓浜ц€?|
| `0x903eac` / `0x904ac1` | 璋冪敤鑰咃紝鎶?bank A/B + 涓や釜 dword 浼犵粰 `0x836fd0`锛堥攢姣侊級 |
| `0x135a81a` | 璁板綍闂存嫹璐?|
| `0xa4b903` / `0xa4b8cb` | **鍋囬槼鎬?*锛氶偅娈垫槸鏁版嵁锛屽弽姹囩紪瑙ｄ笉鍑烘寚浠?|

### 杩欐剰鍛崇潃浠€涔?
**娌℃湁鎵惧埌"鎶?`bank[slot]` 鍙樻垚鍙缃戞牸"鐨勮鑰呫€?* 鎵€浠ュ彞鏌勫緢鍙兘涓嶆槸
娓叉煋鏉ユ簮锛岃€屾槸 `0x81f470` 閲岄偅鍑犳璋冪敤锛坄call [r8+0x770]`銆乣0x81e430`銆?`0x81a650`锛夌湡姝ｆ妸瀹炰緥缁戝畾鍒拌鑹蹭笂锛宐ank 鍙槸缁欓攢姣佺敤鐨勮璐﹁〃銆?
濡傛灉鎴愮珛锛岄偅涔堬細

* **鍔?bank 涓嶄細澧炲姞娓叉煋瀹归噺銆?*
* 18 浠朵笂闄愭潵鑷?*姣忛儴浣嶄竴涓粦瀹氭Ы**锛坰etter 鎸?slot 璁拌处锛夛紝涓嶆槸 bank 鏁扮粍闀垮害銆?* 涔嬪墠"18 浠朵笂闄愪笌 bank 缁撴瀯鍚诲悎"鍙槸鐩稿叧锛屼笉鏄洜鏋溿€?
杩欐潯蹇呴』浼樺厛楠岃瘉锛屽惁鍒欐暣鏉℃墿瀹硅矾绾垮彲鑳藉缓鍦ㄩ敊璇墠鎻愪笂銆?楠岃瘉鏂规硶锛氭墦鏍囪琛ヤ竵璁?`type>=2` 鍏ㄥ啓 `0xDEADBEEF` 杩?bank C锛岀劧鍚?*鎴浘鐪嬬敾闈?*銆?鑻ョ敾闈笉鍙?鈫?bank 涓嶆槸娓叉煋鏉ユ簮锛涜嫢 `type>=2` 鐨勪欢鍏ㄩ儴娑堝け 鈫?bank 鏄覆鏌撴潵婧愩€?锛堣繖涓€姝ョ敤澶栭儴琛ヤ竵鍋氳繃涓€鍗婏紝浣嗘病鎴浘锛涚幇鍦ㄥ繀椤绘敼鐢ㄦ彃浠跺唴 FFI 鎵嶅畨鍏ㄣ€傦級

## 10. 澶嶇幇鏈枃鍏ㄩ儴鏁板瓧

### 7.1 澶嶇幇鏈枃鍏ㄩ儴鏁板瓧

```powershell
# 0) 鎶撳唴瀛橀暅鍍忥紙鐩樹笂 game.dll 鏄３锛孯VA 鍙鍐呭瓨鏈夋剰涔夛級
python HD2RuntimeArmorOverride/scripts/capture_game_image.py --pid <pid> --output <img>

# 1) 鍙嶆眹缂栫敓浜ц€?/ 鎵惧紩鐢?/ 鍖洪棿鎵弿
python HD2RuntimeArmorOverride/scripts/disasm_armor_consumer.py --image <img> `
  --start 0x81f470 --length 0xe00 --head 0 --full --save <txt>
python HD2RuntimeArmorOverride/scripts/find_code_refs.py --image <img> `
  --image-base <base> --target 0x81f470 --dword 0x546b4c --dword 0x546b74 --dword 0x546b9c
python HD2RuntimeArmorOverride/scripts/scan_region.py --image <img> --start <rva> --length <n> --match <regex>

# 2) **鍏堢‘璁ゆ槧灏勭湡鐨勫湪鐢熸晥**锛屽惁鍒欓噺鍒扮殑鏄櫔澹?python HD2RuntimeArmorOverride/scripts/set_protocol_mapping.py `
  --add 820333399:custom-eaf880703fa94f398d620d2288b25d1f

# 3) 閫愮骇鍒囬厤缃紝璇?rec[0] 骞舵埅鍥?python HD2RuntimeArmorOverride/scripts/use_armor_piece_config.py --config C3
python HD2RuntimeArmorOverride/scripts/peek_armor_banks.py --pid <pid> `
  --global-rva 0x3326d20 --indices 0
python HD2RuntimeArmorOverride/scripts/capture_window.py --pid <pid> --output <png>
```

### 7.2 杩樻病鍋氱殑涓や欢浜?
1. **閫愰厤缃娴?*锛氱煡閬撹繍琛屾椂鍐欒繘 Kit 鐨勭粍椤哄簭鍚庯紝鍙互绠楀嚭"鍓?20 涓槸璋?锛?   浠庤€?*棰勬祴**姣忎釜閰嶇疆浼氭帀鍝簺閮ㄤ欢锛屽啀鍜屽疄娴嬪鐓с€傝繖鏄妸 搂6 浠?瑙ｉ噴"鍙樻垚"鍙娴?銆?2. **浜у搧鍖?*锛氭妸"姣忎釜 (Kit, body 鍙樹綋) 鐢熸晥 Piece 鈮?20"鍜?Any 缁勮缁欒韩浣撲欢鐣欏悕棰?
   鍙樻垚绠＄悊鍣?`vpk2patch` 鐨勬牎楠屾垨鑷姩鎺掑竷锛岃瓒呴檺鍦ㄥ鍏ユ椂灏辨姤鍑烘潵锛?   鑰屼笉鏄瓑鐜╁鍦ㄥ啗姊板簱閲岀湅鍒拌兂鑶婁笉瑙佷簡銆?
## 8. 澶嶇幇鏈枃鍏ㄩ儴鏁板瓧鐨勫懡浠?
```powershell
python HD2RuntimeArmorOverride/scripts/capture_game_image.py --pid <pid> --output <img>
python HD2RuntimeArmorOverride/scripts/disasm_armor_consumer.py --image <img> `
  --start 0x81f470 --length 0xe00 --head 0 --full --save <txt>
python HD2RuntimeArmorOverride/scripts/find_code_refs.py --image <img> `
  --image-base <base> --target 0x81f470 --dword 0x546b4c --dword 0x546b74 --dword 0x546b9c
python HD2RuntimeArmorOverride/scripts/scan_region.py --image <img> --start <rva> --length <n> --match <regex>
python HD2RuntimeArmorOverride/scripts/peek_image.py --image <img> --rva <rva> --count 4
python HD2RuntimeArmorOverride/scripts/peek_armor_banks.py --pid <pid> --bulk 4096
```

