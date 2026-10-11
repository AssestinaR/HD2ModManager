"""List every entry in the reference patch container with its type id and sizes."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_patch as P

TOC = ('E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310/'
       '9ba626afa44a3aa3.patch_0')


def main():
    entries, d = P.scan_entries(TOC)
    print('file size %.1f MB, %d entries' % (len(d) / 1048576.0, len(entries)))
    print('%-4s %-18s %-20s %10s %10s %10s' % ('#', 'fileId', 'typeId', 'toc', 'stream', 'gpu'))
    for e in entries:
        print('%-4d 0x%016x %-20s %10d %10d %10d'
              % (e['index'], e['file_id'], '0x%016x' % e['type_id'],
                 e['toc_size'], e['stream_size'], e['gpu_size']))


if __name__ == '__main__':
    main()
