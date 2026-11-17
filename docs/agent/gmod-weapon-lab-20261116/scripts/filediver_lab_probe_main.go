package main

import (
	"fmt"

	datalib "github.com/xypwn/filediver/datalibrary"
)

func main() {
	raw, err := datalib.LabProjectileRaw()
	if err != nil {
		fmt.Println("ERR", err)
		return
	}
	s, err := datalib.LabDumpComponentRecords("ProjectileWeaponComponentData", "ProjectileWeaponComponent", raw)
	fmt.Print(s)
	if err != nil {
		fmt.Println("ERR", err)
	}
}
