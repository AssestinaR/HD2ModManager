#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 .docx 转成 Markdown，并把内嵌图片按原顺序抽出来自动引用。

为什么需要：这类文档（LUT/材质指南）大量信息在截图里——颜色、通道、槽位顺序。
只抽文字会丢掉一半内容，而图片摊在 word/media 里又没有顺序、没法对上上下文。

做法：
  1. `word/_rels/document.xml.rels` 建 rId → 媒体文件 的映射
  2. 按 body 顺序走 <w:p>/<w:tbl>，遇到 <a:blip r:embed="rIdN"> 就插入 ![](media/…)
  3. 图片缩放到不超过 --max-px，另存为 PNG（无损，避免 JPEG 把 LUT 颜色改掉）
  4. 同时在旁边留一份《全文顺序图目录》，方便按编号引用

用法：
    python tools/docx_to_md.py "<in.docx>" --out <out.md> [--media-dir <dir>] [--max-px 1200]
"""
import argparse
import os
import re
import sys
import zipfile
import xml.etree.ElementTree as ET

import cv2

W = '{http://schemas.openxmlformats.org/wordprocessingml/2006/main}'
A = '{http://schemas.openxmlformats.org/drawingml/2006/main}'
R = '{http://schemas.openxmlformats.org/officeDocument/2006/relationships}'
PR = '{http://schemas.openxmlformats.org/package/2006/relationships}'


def load_rels(z):
    rels = {}
    try:
        xml = z.read('word/_rels/document.xml.rels')
    except KeyError:
        return rels
    root = ET.fromstring(xml)
    for rel in root.findall(PR + 'Relationship'):
        rels[rel.get('Id')] = rel.get('Target')
    return rels


def para_text(p):
    parts = []
    for node in p.iter():
        if node.tag == W + 't':
            parts.append(node.text or '')
        elif node.tag == W + 'tab':
            parts.append('\t')
        elif node.tag == W + 'br':
            parts.append('\n')
    return ''.join(parts)


def para_style(p):
    ppr = p.find(W + 'pPr')
    if ppr is None:
        return ''
    st = ppr.find(W + 'pStyle')
    return st.get(W + 'val') if st is not None else ''


def para_blips(p):
    out = []
    for node in p.iter():
        if node.tag == A + 'blip':
            rid = node.get(R + 'embed')
            if rid:
                out.append(rid)
    # 老式 <w:pict><v:imagedata r:id>
    for node in p.iter():
        if node.tag.endswith('}imagedata'):
            rid = node.get(R + 'id')
            if rid:
                out.append(rid)
    return out


def convert_image(src_path, dst_path, max_px):
    img = cv2.imread(src_path, cv2.IMREAD_UNCHANGED)
    if img is None:
        return False, None
    h, w = img.shape[:2]
    if max(h, w) > max_px:
        scale = max_px / float(max(h, w))
        img = cv2.resize(img, (max(1, int(w * scale)), max(1, int(h * scale))), interpolation=cv2.INTER_AREA)
    cv2.imwrite(dst_path, img, [cv2.IMWRITE_PNG_COMPRESSION, 9])
    return True, (img.shape[1], img.shape[0])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('docx')
    ap.add_argument('--out', required=True)
    ap.add_argument('--media-dir', default=None)
    ap.add_argument('--max-px', type=int, default=1200)
    args = ap.parse_args()

    out_dir = os.path.dirname(os.path.abspath(args.out))
    media_dir = args.media_dir or os.path.join(out_dir, os.path.splitext(os.path.basename(args.out))[0] + '_media')
    os.makedirs(media_dir, exist_ok=True)

    with zipfile.ZipFile(args.docx) as z:
        rels = load_rels(z)
        root = ET.fromstring(z.read('word/document.xml'))
        body = root.find(W + 'body')
        if body is None:
            raise SystemExit('找不到 word/body')

        # 先把用到的媒体按出现顺序编号并导出
        used_order = []
        seen = set()

        def note(rid):
            tgt = rels.get(rid)
            if not tgt or tgt in seen:
                return None
            seen.add(tgt)
            used_order.append(tgt)
            return tgt

        lines = []
        blank = False
        fig_no = 0
        exported = {}

        def export(tgt):
            nonlocal fig_no
            if tgt in exported:
                return exported[tgt]
            src = 'word/' + tgt.replace('\\', '/')
            base, ext = os.path.splitext(os.path.basename(tgt))
            dst_name = '%03d_%s.png' % (len(exported) + 1, base)
            dst = os.path.join(media_dir, dst_name)
            with zipfile.ZipFile(args.docx) as zz:
                tmp = os.path.join(media_dir, '_tmp' + ext)
                with open(tmp, 'wb') as f:
                    f.write(zz.read(src))
            ok, dim = convert_image(tmp, dst, args.max_px)
            os.remove(tmp)
            exported[tgt] = (dst_name, dim)
            return exported[tgt]

        def emit_figures(blips):
            nonlocal fig_no
            for rid in blips:
                tgt = note(rid)
                if not tgt:
                    continue
                name, dim = export(tgt)
                fig_no += 1
                size = ' (%dx%d)' % dim if dim else ''
                lines.append('![图 %d](%s/%s)%s' % (fig_no, os.path.basename(media_dir), name, size))
                lines.append('')

        def walk(container):
            for child in container:
                tag = child.tag
                if tag == W + 'p':
                    text = para_text(child).strip()
                    style = para_style(child)
                    blips = para_blips(child)
                    if text:
                        m = re.match(r'^(?:Heading|heading)(\d)$', style)
                        if m:
                            lines.append('#' * int(m.group(1)) + ' ' + text)
                        elif 'ListParagraph' in style:
                            lines.append('- ' + text)
                        else:
                            lines.append(text)
                        blank_local = False
                    if blips:
                        emit_figures(blips)
                    if not text and not blips:
                        lines.append('')
                elif tag == W + 'tbl':
                    rows = []
                    for tr in child.findall(W + 'tr'):
                        cells = []
                        for tc in tr.findall(W + 'tc'):
                            cell = ' '.join(para_text(p).strip() for p in tc.findall(W + 'p')).strip()
                            cells.append(cell.replace('|', '\\|').replace('\n', ' '))
                        rows.append(cells)
                    if rows:
                        width = max(len(r) for r in rows)
                        rows = [r + [''] * (width - len(r)) for r in rows]
                        lines.append('| ' + ' | '.join(rows[0]) + ' |')
                        lines.append('|' + '---|' * width)
                        for r in rows[1:]:
                            lines.append('| ' + ' | '.join(r) + ' |')
                        lines.append('')

        walk(body)

    # 收尾：压掉连续空行
    cleaned = []
    for line in lines:
        if line == '' and cleaned and cleaned[-1] == '':
            continue
        cleaned.append(line)
    text = '\n'.join(cleaned).strip() + '\n'

    with open(args.out, 'w', encoding='utf-8') as f:
        f.write(text)

    print('正文字符 %d，图片 %d 张（已导出到 %s）' % (len(text), len(exported), media_dir))
    print('Markdown: %s' % args.out)
    return 0


if __name__ == '__main__':
    sys.exit(main())
