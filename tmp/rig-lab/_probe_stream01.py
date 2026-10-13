# -*- coding: utf-8 -*-
"""Why does `stream 1 mirrors stream 0` fail?

Hypothesis: `VertexBufferSize` includes the 16-byte alignment padding, and
stream 1's vertex buffer starts at a *non*-aligned offset (it follows stream 0's
index buffer, which is 478089*4 = 1912356 bytes -> not a multiple of 16).  So
stream 1's declared size is 12 bytes larger than its real payload, and a naive
`gpu_vertex[0] == gpu_vertex[1]` compares a padded buffer with an unpadded one.
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_patch as HP
import hd2_unit as U

TOC = r'E:\mod\Lunalice-RigTest\9ba626afa44a3aa3.patch_0'

entries, raw = HP.scan_entries(TOC)
ue = [e for e in entries if e['type_id'] == U.UNIT_ID][0]
pl = HP.entry_payload(ue, TOC)
u = U.Unit.read(pl['toc'], pl['gpu'])

print('gpu blob : %d bytes' % len(pl['gpu']))
for i, s in enumerate(u.stream_infos):
    print('s%d  vb off=%-10d size=%-10d  ib off=%-10d size=%-10d  nv=%-7d stride=%d ni=%d'
          % (i, s.vertex_buffer_offset, s.vertex_buffer_size,
             s.index_buffer_offset, s.index_buffer_size,
             s.num_vertices, s.vertex_stride, s.num_indices))

s0, s1 = u.stream_infos[0], u.stream_infos[1]
n_real = s0.num_vertices * s0.vertex_stride
print()
print('s0 raw vb len        : %d' % len(u.gpu_vertex[0]))
print('s1 raw vb len        : %d' % len(u.gpu_vertex[1]))
print('real payload (nv*str): %d' % n_real)
print('s1 vb start %% 16     : %d' % (s1.vertex_buffer_offset % 16))
print('s1 padding tail      : %d' % (s1.vertex_buffer_size - n_real))
print()
print('vb equal (full)      : %s' % (u.gpu_vertex[0] == u.gpu_vertex[1]))
print('vb equal (payload)   : %s' % (u.gpu_vertex[0][:n_real] == u.gpu_vertex[1][:n_real]))
print('s1 tail all zero     : %s' % (u.gpu_vertex[1][n_real:] == b'\x00' * (s1.vertex_buffer_size - n_real)))
print('ib equal             : %s' % (u.gpu_index[0] == u.gpu_index[1]))
print('ib len               : %d / %d' % (len(u.gpu_index[0]), len(u.gpu_index[1])))

# cross-check against the reference unit: is the padding convention the same?
REF = r'E:\mod\Lunalice-Visible-Virtual-Model\9ba626afa44a3aa3.patch_0'
re_entries, _ = HP.scan_entries(REF)
rue = [e for e in re_entries if e['type_id'] == U.UNIT_ID and e['file_id'] == 0xD841B16544C8FBAE][0]
rpl = HP.entry_payload(rue, REF)
ru = U.Unit.read(rpl['toc'], rpl['gpu'])
print()
print('reference unit 0xd841b16544c8fbae')
for i, s in enumerate(ru.stream_infos):
    print('s%d  vb off=%-10d size=%-10d  ib off=%-10d size=%-10d  nv=%-7d stride=%d ni=%d'
          % (i, s.vertex_buffer_offset, s.vertex_buffer_size,
             s.index_buffer_offset, s.index_buffer_size,
             s.num_vertices, s.vertex_stride, s.num_indices))
    if s.vertex_buffer_size:
        pad = s.vertex_buffer_size - s.num_vertices * s.vertex_stride
        print('     -> padding in declared size: %d (start %% 16 = %d)'
              % (pad, s.vertex_buffer_offset % 16))
