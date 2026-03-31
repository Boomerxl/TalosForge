using System;
using System.IO;
using System.Text;

var dllPath = @"C:\Windows\SysWOW64\kernel32.dll";
var targetExport = "InitializeSListHead";
var bytes = File.ReadAllBytes(dllPath);

Console.WriteLine($"File: {dllPath} ({bytes.Length} bytes)");

var lfanew = BitConverter.ToInt32(bytes, 0x3C);
var opt = lfanew + 4 + 20;
var magic = BitConverter.ToUInt16(bytes, opt);
Console.WriteLine($"e_lfanew=0x{lfanew:X}, opt magic=0x{magic:X4}");

var numSections = BitConverter.ToUInt16(bytes, lfanew + 4 + 2);
var optHeaderSize = BitConverter.ToUInt16(bytes, lfanew + 4 + 16);
var sectionStart = opt + optHeaderSize;
Console.WriteLine($"Sections: {numSections}, sectionStart=0x{sectionStart:X}");

for (int i = 0; i < numSections; i++) {
    var so = sectionStart + i * 40;
    var name = Encoding.ASCII.GetString(bytes, so, 8).TrimEnd('\0');
    var vsize = BitConverter.ToUInt32(bytes, so + 8);
    var va = BitConverter.ToUInt32(bytes, so + 12);
    var rawSz = BitConverter.ToUInt32(bytes, so + 16);
    var rawPtr = BitConverter.ToUInt32(bytes, so + 20);
    Console.WriteLine($"  [{i}] {name,-8} VA=0x{va:X8} VSize=0x{vsize:X} RawPtr=0x{rawPtr:X} RawSize=0x{rawSz:X}");
}

var expRva = BitConverter.ToUInt32(bytes, opt + 96);
var expSize = BitConverter.ToUInt32(bytes, opt + 100);
Console.WriteLine($"\nExport dir RVA=0x{expRva:X} Size={expSize}");

int RvaToFile(uint rva) {
    for (int i = 0; i < numSections; i++) {
        var so = sectionStart + i * 40;
        var va = BitConverter.ToUInt32(bytes, so + 12);
        var rawSz = BitConverter.ToUInt32(bytes, so + 16);
        var rawPtr = BitConverter.ToUInt32(bytes, so + 20);
        var vsize = BitConverter.ToUInt32(bytes, so + 8);
        var end = va + Math.Max(rawSz, vsize);
        if (rva >= va && rva < end)
            return (int)(rawPtr + (rva - va));
    }
    return -1;
}

var expOff = RvaToFile(expRva);
Console.WriteLine($"Export dir file offset=0x{expOff:X}");
if (expOff < 0) { Console.WriteLine("FAILED to convert export dir RVA"); return; }

var numFunctions = BitConverter.ToUInt32(bytes, expOff + 20);
var numNames = BitConverter.ToUInt32(bytes, expOff + 24);
var addrOfFunctions = BitConverter.ToUInt32(bytes, expOff + 28);
var addrOfNames = BitConverter.ToUInt32(bytes, expOff + 32);
var addrOfOrdinals = BitConverter.ToUInt32(bytes, expOff + 36);
Console.WriteLine($"numFunctions={numFunctions} numNames={numNames}");
Console.WriteLine($"addrOfNames RVA=0x{addrOfNames:X} addrOfOrdinals RVA=0x{addrOfOrdinals:X} addrOfFunctions RVA=0x{addrOfFunctions:X}");

var namesOff = RvaToFile(addrOfNames);
var ordinalsOff = RvaToFile(addrOfOrdinals);
var functionsOff = RvaToFile(addrOfFunctions);
Console.WriteLine($"namesFileOff=0x{namesOff:X} ordinalsFileOff=0x{ordinalsOff:X} functionsFileOff=0x{functionsOff:X}");

if (namesOff < 0 || ordinalsOff < 0 || functionsOff < 0) {
    Console.WriteLine("FAILED to convert table RVAs");
    return;
}

bool found = false;
for (uint i = 0; i < numNames && i < 5000; i++) {
    var nameRva = BitConverter.ToUInt32(bytes, namesOff + (int)i * 4);
    var nameOff = RvaToFile(nameRva);
    if (nameOff < 0) continue;
    var end = nameOff;
    while (end < bytes.Length && bytes[end] != 0) end++;
    var name = Encoding.ASCII.GetString(bytes, nameOff, end - nameOff);
    if (name == targetExport) {
        var ordinal = BitConverter.ToUInt16(bytes, ordinalsOff + (int)i * 2);
        var fRva = BitConverter.ToUInt32(bytes, functionsOff + ordinal * 4);
        Console.WriteLine($"\nFOUND '{targetExport}' at index={i} ordinal={ordinal} funcRVA=0x{fRva:X}");
        if (fRva >= expRva && fRva < expRva + expSize) {
            var fwdOff = RvaToFile(fRva);
            if (fwdOff >= 0) {
                var fe = fwdOff;
                while (fe < bytes.Length && bytes[fe] != 0) fe++;
                var fwd = Encoding.ASCII.GetString(bytes, fwdOff, fe - fwdOff);
                Console.WriteLine($"  FORWARDED to: {fwd}");
            }
        }
        found = true;
        break;
    }
}
if (!found) Console.WriteLine($"\n'{targetExport}' NOT FOUND in {numNames} exports");
