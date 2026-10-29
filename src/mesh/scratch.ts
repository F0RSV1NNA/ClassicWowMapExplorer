import type { PageStore } from './stream-analysis';

interface SyncFile {
	write(bytes: Uint8Array, options: { at: number }): number;
	truncate(size: number): void; flush(): void; close(): void;
}
/** Scratch pages live in OPFS, not in a Wasm virtual filesystem or the chosen export.
 * Handles are closed after each write, and the run's own directory is removed on exit. */
export async function createScratch(): Promise<PageStore & { name: string; close(): Promise<void>; bytesWritten(): number }> {
	const root = await navigator.storage.getDirectory();
	// A terminated worker releases its Web Lock. Reclaim only our own abandoned runs,
	// and never remove a directory owned by an analysis in another tab.
	for await (const [old, handle] of root.entries()) if (handle.kind === 'directory' && /^mesh-analysis-[0-9a-f-]{36}$/.test(old)) {
		await navigator.locks.request(`mesh-scratch:${old}`, { ifAvailable: true }, async lock => {
			if (lock) await root.removeEntry(old, { recursive: true }).catch(() => {});
		});
	}
	const name = `mesh-analysis-${crypto.randomUUID()}`;
	let release!: () => void;
	await new Promise<void>((resolve, reject) => {
		void navigator.locks.request(`mesh-scratch:${name}`, async () => { resolve(); await new Promise<void>(r => { release = r; }); }).catch(reject);
	});
	const directory = await root.getDirectoryHandle(name, { create: true });
	let written = 0;
	return {
		name,
		async put(key, bytes) {
			const handle = await directory.getFileHandle(key, { create: true }) as FileSystemFileHandle & { createSyncAccessHandle(): Promise<SyncFile> };
			const file = await handle.createSyncAccessHandle();
			try {
				let at = 0;
				while (at < bytes.length) { const n = file.write(bytes.subarray(at), { at }); if (!n) throw new Error('Scratch write made no progress'); at += n; }
				file.truncate(bytes.length); file.flush(); written += bytes.length;
			} finally { file.close(); }
		},
		async get(key) { return new Uint8Array(await (await (await directory.getFileHandle(key)).getFile()).arrayBuffer()); },
		async close() { try { await root.removeEntry(name, { recursive: true }); } finally { release(); } },
		bytesWritten() { return written; },
	};
}
