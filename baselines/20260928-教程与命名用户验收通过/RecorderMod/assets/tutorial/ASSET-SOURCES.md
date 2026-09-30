# 教程角色素材来源

最终使用：`arona-guide.png` 与 `arona-page-02.png` 至 `arona-page-06.png`，共六张。它们是使用内置 imagegen 对公开官方阿罗娜参考图进行修改的同人教程素材，不是官方原图。编译调试 DLL 时作为图片资源嵌入，教程运行不需要联网。原参考图保持原样。

官方来源：

- Blue Archive 日本官网 [FANKIT](https://bluearchive.jp/fankit) → スタンプ → サークルスタンプ 03。
- [阿罗娜官方贴纸](https://webusstatic.yo-star.com/bluearchive_jp_web/fankit/169320913647213804/01.png)，保存为 `reference/arona-official-stamp.png`。
- [动画官方阿罗娜角色页](https://sh-anime.shochiku.co.jp/bluearchive-anime/character/arona/)及其[正面立绘](https://sh-anime.shochiku.co.jp/bluearchive-anime/assets/images/common/character/arona/chara-arona-front_03.png)，保存为 `reference/arona-official-standing.png`。
- 角色及原美术权利归对应权利人；官方页面署名为 NEXON Games / Yostar，以及 NEXON Games／アビドス商店街。参考来源保留于此，不声称原创或官方授权。

用户要求光环必须参考角色立绘。初次生图的粉色光环不正确，已弃用；最终版本以官方立绘校正为蓝色、水平悬浮的环形光环，并去掉错误的侧辫。

## 六页对应动作与界面参考

用户随后要求每页动作、表情都不同；各页均以修正光环后的 `arona-guide.png` 和官方立绘为参考，使用内置 `image_gen` 独立编辑，保留真实透明通道。完整新增提示词见 `pose-prompts.json`；第 4 页为强化思考表情追加的最终提示词见 `pose-04-refinement.json`。

| 页面 | 文件 | 动作与表情 |
| --- | --- | --- |
| 1 · 分辨率 | arona-guide.png | 挥手欢迎，睁眼开心笑 |
| 2 · 视频帧率 | arona-page-02.png | 举指讲解，专注自信微笑 |
| 3 · 保存位置 | arona-page-03.png | 双手展示文件夹，眨眼笑 |
| 4 · DLSS 开关 | arona-page-04.png | 托腮抱平板，抬眼思考、小圆嘴 |
| 5 · 开始内录 | arona-page-05.png | 握拳鼓励，坚定眉眼与张口笑 |
| 6 · 教程回看 | arona-page-06.png | 双手身前轻鞠躬，闭眼微笑 |

用户提供的官方教程界面截图另存为 `reference/official-guide-layout.png`，只作为暗色背景、蓝白高亮框、黄色箭头与点击目标前进的界面参考，不作为打包或联网资源。各页目标区域使用原设置控件的真实画面；上层代理接收教程点击，背景功能均被拦截，右上角跳过不受暗化遮罩影响。

## 内置生图提示词记录

初稿（未用于 MOD）：

> Use case: precise-object-edit. Asset type: transparent chibi tutorial assistant sprite in an Azure Archive / Blue Archive native-style recording MOD. Edit the attached official Arona stamp reference into a friendly tutorial guide Arona. Preserve her recognizable light blue bobbed hair with hair ornament, white-and-blue sailor uniform, cute chibi proportions, navy rounded line art, pastel cel coloring and light blush. Remove ALL Japanese text, confetti, birthday hat, party whistle, and birthday cake. Show her full body, smiling with eyes open, one hand giving a small welcoming wave and the other holding a simple pale blue tablet against her chest. The tablet is blank. Keep the halo and bow-like hair feature consistent with Arona. Modest outfit, sweet helpful expression, clean silhouette, high quality readable at 240 pixels tall. Portrait composition centered with generous transparent margin, no cropping of halo or feet. Genuine transparent alpha background, no floor, no shadows, no white square, no words, no lettering, no logo, no watermark. This is a fan MOD tutorial illustration derived from the provided reference, not an official asset.

最终修正（输入：初稿、官方正面立绘；transparent_background=true）：

> Use case: precise-object-edit. Image 1 is the edit target, an Arona chibi guide asset. Image 2 is the AUTHORITATIVE OFFICIAL standing reference for Arona, especially her halo and hair. Correct Image 1 to match Image 2. MOST IMPORTANT: remove the wrong tall pink balloon/heart symbol above the head entirely. Replace it with Arona's official CYAN BLUE HORIZONTAL RING HALO, floating clearly ABOVE her white hair ribbon, elliptical only due to viewing perspective, slim double-ring cyan outline with small blue digital segment details as visible in Image 2. It is a flat ring/disk tilted slightly in perspective, no pink, no heart, no question mark, no protruding stem. Keep the halo proportional as in Image 2, not giant, and separate from the hair bow. Also remove the incorrect long side braid from Image 1: Arona has a short light blue bob with pink inner hair tips, as in official Image 2. Keep the chibi navy hand-drawn line style, simple shading, gentle smile, waving hand, blank tablet, modest blue and white sailor outfit and full body of Image 1. No other character. No letters, no text, no confetti. True transparent alpha background, preserve high quality edges and generous margin around the whole character including the halo and shoes. Final game UI asset, portrait layout.
