﻿/*
    Copyright (C) 2014-2018 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.AntiAntiDebug;

namespace dnSpy.Debugger.AntiAntiDebug {
	sealed class DbgHookedNativeFunctionProviderImpl : DbgHookedNativeFunctionProvider {
		readonly DbgProcess process;
		readonly ProcessMemoryBlockAllocator processMemoryBlockAllocator;
		readonly Process netProcess;
		readonly Dictionary<string, ModuleInfo> toModuleInfo;
		readonly HashSet<(string dllName, string funcName)> hookedFuncs;
		readonly List<SimpleAPIPatch> simplePatches;

		sealed class ModuleInfo {
			public string Name { get; }
			public string Filename { get; }
			public ulong Address { get; }
			public ulong EndAddress { get; }
			public ExportedFunctions ExportedFunctions { get; set; }

			public ModuleInfo(ProcessModule module) {
				Name = Path.GetFileName(module.FileName);
				Filename = module.FileName;
				Address = (ulong)module.BaseAddress.ToInt64();
				EndAddress = Address + (uint)module.ModuleMemorySize;
			}
		}

		public DbgHookedNativeFunctionProviderImpl(DbgProcess process, ProcessMemoryBlockAllocator processMemoryBlockAllocator) {
			this.process = process ?? throw new ArgumentNullException(nameof(process));
			this.processMemoryBlockAllocator = processMemoryBlockAllocator ?? throw new ArgumentNullException(nameof(processMemoryBlockAllocator));
			netProcess = Process.GetProcessById(process.Id);
			toModuleInfo = new Dictionary<string, ModuleInfo>(netProcess.Modules.Count, StringComparer.OrdinalIgnoreCase);
			hookedFuncs = new HashSet<(string dllName, string funcName)>();
			simplePatches = new List<SimpleAPIPatch>();
			foreach (ProcessModule module in netProcess.Modules) {
				var info = new ModuleInfo(module);
				toModuleInfo[info.Name] = info;
			}
		}

		public override DbgHookedNativeFunction GetFunction(string dllName, string funcName) {
			if (dllName == null)
				throw new ArgumentNullException(nameof(dllName));
			if (funcName == null)
				throw new ArgumentNullException(nameof(funcName));
			if (!toModuleInfo.TryGetValue(dllName, out var info))
				throw new DbgHookException($"Couldn't find DLL {dllName}");
			if (!hookedFuncs.Add((dllName, funcName)))
				throw new DbgHookException($"Some code tried to hook the same func twice: {dllName}: {funcName}");
			if (info.ExportedFunctions == null) {
				using (var reader = new ExportedFunctionsReader(info.Filename, info.Address))
					info.ExportedFunctions = reader.ReadExports();
			}
			if (info.ExportedFunctions.TryGet(funcName, out var address)) {
				var result = PatchAPI(address, info.Address, info.EndAddress);
				if (result.ErrorMessage != null)
					throw new DbgHookException(result.ErrorMessage);
				simplePatches.Add(result.SimplePatch);
				return new DbgHookedNativeFunctionImpl(result.Block, result.NewFunctionAddress, address);
			}
			throw new DbgHookException($"Couldn't find function {funcName} in {dllName}");
		}

		public override DbgHookedNativeFunction GetFunction(string dllName, string funcName, ulong address) {
			if (dllName == null)
				throw new ArgumentNullException(nameof(dllName));
			if (funcName == null)
				throw new ArgumentNullException(nameof(funcName));
			if (!hookedFuncs.Add((dllName, funcName)))
				throw new DbgHookException($"Some code tried to hook the same func twice: {dllName}: {funcName}");
			var result = PatchAPI(address, address, address + 1);
			if (result.ErrorMessage != null)
				throw new DbgHookException(result.ErrorMessage);
			simplePatches.Add(result.SimplePatch);
			return new DbgHookedNativeFunctionImpl(result.Block, result.NewFunctionAddress, address);
		}

		PatchAPIResult PatchAPI(ulong address, ulong moduleAddress, ulong moduleEndAddress) {
			switch (process.Machine) {
			case DbgMachine.X86:
			case DbgMachine.X64:
				var patcherX86 = new ApiPatcherX86(processMemoryBlockAllocator, is64: process.Machine == DbgMachine.X64, address, moduleAddress, moduleEndAddress);
				return patcherX86.Patch();

			default:
				Debug.Fail($"Unsupported machine: {process.Machine}");
				return new PatchAPIResult($"Unsupported machine: {process.Machine}");
			}
		}

		public override bool TryGetModuleAddress(string dllName, out ulong address, out ulong endAddress) {
			if (dllName == null)
				throw new ArgumentNullException(nameof(dllName));
			if (toModuleInfo.TryGetValue(dllName, out var info)) {
				address = info.Address;
				endAddress = info.EndAddress;
				return true;
			}

			address = 0;
			endAddress = 0;
			return false;
		}

		public override bool TryGetFunction(string dllName, string funcName, out ulong address) {
			if (dllName == null)
				throw new ArgumentNullException(nameof(dllName));
			if (funcName == null)
				throw new ArgumentNullException(nameof(funcName));
			if (!toModuleInfo.TryGetValue(dllName, out var info)) {
				address = 0;
				return false;
			}
			return info.ExportedFunctions.TryGet(funcName, out address);
		}

		public void Write() {
			foreach (var patch in simplePatches)
				process.WriteMemory(patch.Address, patch.Data);
		}

		public void Dispose() => netProcess?.Dispose();
	}
}
