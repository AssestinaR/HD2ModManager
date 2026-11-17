package datalib

// LAB ONLY (temp clone): dump ProjectileWeaponComponent layout from typelib and decode selected members by typelib offsets.
import (
	"bytes"
	"encoding/binary"
	"fmt"
	"io"

	"github.com/xypwn/filediver/stingray"
)

func LabProbeProjectile(target stingray.Hash, want []string) (string, error) {
	tl, err := ParseTypeLib(nil)
	if err != nil {
		return "", err
	}
	out := ""
	t := tl.Types[Sum("ProjectileWeaponComponent")]
	out += fmt.Sprintf("typelib ProjectileWeaponComponent size=%d members=%d ; go struct binary.Size=%d\n", t.Size, len(t.Members), binary.Size(ProjectileWeaponComponent{}))
	for _, m := range t.Members {
		out += fmt.Sprintf("  @%4d size=%4d %s\n", m.Offset, m.Size, m.Name)
	}
	data, err := getProjectileWeaponComponentData()
	if err != nil {
		return out, err
	}
	dt := tl.Types[Sum("ProjectileWeaponComponentData")]
	n0 := dt.Members[0].Type.BitfieldInfoOrArrayLen.GetArrayLen()
	n1 := dt.Members[1].Type.BitfieldInfoOrArrayLen.GetArrayLen()
	r := bytes.NewReader(data)
	hm := make([]ComponentIndexData, n0)
	if err := binary.Read(r, binary.LittleEndian, &hm); err != nil {
		return out, err
	}
	base, _ := r.Seek(0, io.SeekCurrent)
	out += fmt.Sprintf("entries=%d datacount=%d base=%d total=%d expected=%d\n", n0, n1, base, len(data), int(base)+int(n1)*int(t.Size))
	for _, e := range hm {
		if e.Resource != target {
			continue
		}
		rec := data[int(base)+int(e.Index)*int(t.Size):][:t.Size]
		for _, m := range t.Members {
			for _, w := range want {
				if m.Name == w {
					out += fmt.Sprintf("  %s @%d = % x\n", m.Name, m.Offset, rec[m.Offset:m.Offset+m.Size])
				}
			}
		}
	}
	return out, nil
}

// LabDumpComponentRecords writes each record of <TypeName>Data (hashmap + inline array) as hex per resource.
func LabDumpComponentRecords(dataTypeName, compTypeName string, raw []byte) (string, error) {
	tl, err := ParseTypeLib(nil)
	if err != nil {
		return "", err
	}
	t := tl.Types[Sum(compTypeName)]
	dt := tl.Types[Sum(dataTypeName)]
	n0 := dt.Members[0].Type.BitfieldInfoOrArrayLen.GetArrayLen()
	r := bytes.NewReader(raw)
	hm := make([]ComponentIndexData, n0)
	if err := binary.Read(r, binary.LittleEndian, &hm); err != nil {
		return "", err
	}
	base, _ := r.Seek(0, io.SeekCurrent)
	out := fmt.Sprintf("#size %d\n", t.Size)
	for _, e := range hm {
		if e.Resource.Value == 0 {
			continue
		}
		rec := raw[int(base)+int(e.Index)*int(t.Size):][:t.Size]
		out += fmt.Sprintf("%016x %x\n", e.Resource.Value, rec)
	}
	return out, nil
}

func LabProjectileRaw() ([]byte, error) { return getProjectileWeaponComponentData() }
