import {CdnReader}from './cdn';
import { decodeBlte, EncryptedError, type BlteResult } from './blte';
import { readBuildInfo, readConfig, type ProductInfo } from './config';
import { EncodingTable } from './encoding';
import { hashPath } from './jenkins96';
import { LocalIndex, bucketOf } from './localIndex';
import { fromHex, toHex } from './reader';
import { RootTable } from './root';
import { NotFoundError, type FileSource, type RandomAccessFile } from './source';

/** Size of the header in front of each file in a data.### archive. */
const ARCHIVE_ENTRY_HEADER = 0x1e;
/** Index files are named BBvvvvvvvv.idx (bucket, version). */
const IDX_NAME = /^([0-9a-f]{2})([0-9a-f]{8})\.idx$/i;

export type FileStatus = 'ok' | 'unknown' | 'no-encoding' | 'not-local';

export interface StorageStats {
	product: string;
	version: string;
	buildName: string;
    buildKey?:string;
    rootContentKey?:string;
    rootSha256?:string;
    readerVersion?:string;
	indexEntries: number;
	encodingPages: number;
	rootFiles: number;
	rootNamedFiles: number;
	timings: Record<string, number>;
}

export class NotLocalError extends Error {
	constructor(what: string, detail = '') {
		super(`${what} is not in local storage${detail}`);
		this.name = 'NotLocalError';
	}
}

/**
 * The game was updated (or repaired) since the storage was opened: its index files were replaced,
 * so the archives no longer hold what the index read at the start says.
 */
export class StorageChangedError extends Error {
	constructor(what: string) {
		super(`${what}: the game was updated while Map Explorer was open; reload to read the new files`);
		this.name = 'StorageChangedError';
	}
}

export { EncryptedError };

/** Reads files by encoding key from the data.### archives. */
class ArchiveReader {
	private readonly archives = new Map<number, Promise<RandomAccessFile>>();
	/** Set once the install is seen to have changed; every read fails from then on. */
	private changed = false;
	/** Whether any read has worked yet. */
	private hasRead = false;
	/** A check under way, shared by the reads that fail meanwhile. */
	private checking: Promise<boolean> | null = null;
	/** Called once, when the install is found to have changed. */
	onChanged: () => void = () => {};

	/** idxNames: the index files Data/data held when the index was read. */
	constructor(private readonly source: FileSource, readonly index: LocalIndex, private readonly idxNames: string[],private readonly cdn?:CdnReader) {}
    get remoteAvailable(){return !!this.cdn;}

	async read(ekey: Uint8Array, what: string, allowPartial = false): Promise<BlteResult> {
		if (this.changed) throw new StorageChangedError(what);
		try {
			const result = await this.readEntry(ekey, what, allowPartial);
			this.hasRead = true;
			return result;
		} catch (e) {
			// A file that isn't stored or is encrypted says nothing about the install changing.
			if((e instanceof NotLocalError||e instanceof NotFoundError)&&this.cdn)return decodeBlte(await this.cdn.read(ekey),allowPartial);
            if (e instanceof NotLocalError || e instanceof EncryptedError) throw e;
			// A browser refuses to read a picked file that has changed since; a folder list picked
			// with an <input> stays as it was, so this is the only sign there. Before any read has
			// worked, it is more likely the folder's permissions.
			if (this.hasRead && e instanceof DOMException && e.name === 'NotReadableError') this.markChanged();
			if (!await this.hasChanged()) throw e;
			throw new StorageChangedError(what);
		}
	}

	private markChanged(): void {
		if (this.changed) return;
		this.changed = true;
		this.onChanged();
	}

	/** Whether Data/data's index files differ from those read at the start; an update replaces them. */
	private hasChanged(): Promise<boolean> {
		if (this.changed) return Promise.resolve(true);
		this.checking ??= (async () => {
			try {
				const now = (await this.source.listDir(['Data', 'data'])).filter((name) => IDX_NAME.test(name)).sort();
				const changed = now.join() !== this.idxNames.join();
				if (changed) this.markChanged();
				return changed;
			} catch {
				return false;
			} finally {
				this.checking = null;
			}
		})();
		return this.checking;
	}

	private async readEntry(ekey: Uint8Array, what: string, allowPartial: boolean): Promise<BlteResult> {
		const entry = this.index.find(ekey);
		if (!entry) {
			const bucket = bucketOf(ekey);
			throw new NotLocalError(what, ` (key ${toHex(ekey)}, bucket ${bucket}; index has ${this.index.entryCount} entries in buckets ${this.index.loadedBuckets})`);
		}
		const archive = await this.archive(entry.archive);
		const bytes = await archive.read(entry.offset + ARCHIVE_ENTRY_HEADER, entry.size - ARCHIVE_ENTRY_HEADER);
		try {
			return decodeBlte(bytes, allowPartial);
		} catch (e) {
			if (!(e instanceof Error) || !e.message.startsWith('Not a BLTE stream')) throw e;
			// The entry header holds the key reversed; if it differs, the index points at the wrong place.
			const header = await archive.read(entry.offset, ARCHIVE_ENTRY_HEADER);
			// Some installs (seen with Forever Beta) store the stream right at the offset, with no entry header.
			if (header[0] === 0x42 && header[1] === 0x4c && header[2] === 0x54 && header[3] === 0x45) {
				return decodeBlte(await archive.read(entry.offset, Math.min(entry.size, archive.size - entry.offset)), allowPartial);
			}
			const headerKey = toHex(header.subarray(0, 16).slice().reverse());
			const where = `data.${entry.archive.toString().padStart(3, '0')} offset ${entry.offset} size ${entry.size}` +
				` (archive is ${archive.size} bytes)`;
			const keyNote = headerKey.startsWith(toHex(ekey.subarray(0, 9)))
				? 'entry header key matches, so the data itself is damaged or unexpected'
				: `entry header key is ${headerKey}, not ${toHex(ekey)}, so the entry is not where the index says or has an unknown layout`;
			throw new Error(`${what}: ${e.message} at ${where}; ${keyNote}`);
		}
	}

	private archive(n: number): Promise<RandomAccessFile> {
		let file = this.archives.get(n);
		if (!file) {
			file = this.source.openFile(['Data', 'data', `data.${n.toString().padStart(3, '0')}`]);
			this.archives.set(n, file);
		}
		return file;
	}
}

/** Read-only view of a local CASC storage for one product (e.g. wow_classic_beta). */
export class CascStorage {
	private constructor(
		private readonly archives: ArchiveReader,
		private readonly encoding: EncodingTable,
		private readonly root: RootTable,
		readonly stats: StorageStats,
	) {}

	/**
	 * The products in .build.info, then (inactive) any build in Data/config it doesn't list: an
	 * install can hold a product's data after the launcher has dropped it from .build.info.
	 */
	static async listProducts(source: FileSource): Promise<ProductInfo[]> {
		const listed = await readBuildInfo(source);
		const known = new Set(listed.map((p) => p.buildKey.toLowerCase()));
		const unlisted: ProductInfo[] = [];
		for (const key of await listConfigKeys(source)) {
			if (known.has(key)) continue;
			let config: Map<string, string[]>;
			try {
				config = await readConfig(source, key);
			} catch {
				continue;
			}
			const product = config.get('build-uid')?.[0];
			if (!product || !config.has('root')) continue;
			unlisted.push({ product, version: config.get('build-name')?.join(' ') ?? '', branch: '', active: false, buildKey: key, cdnKey: '' });
		}
		return [...listed, ...unlisted];
	}

	static async open(source: FileSource, product: string, onProgress: (msg: string) => void = () => {}): Promise<CascStorage> {
		const timings: Record<string, number> = {};
		const time = async <T>(label: string, fn: () => Promise<T>): Promise<T> => {
			onProgress(label);
			const t = performance.now();
			const result = await fn();
			timings[label] = Math.round(performance.now() - t);
			return result;
		};

		const products = await CascStorage.listProducts(source);
		// Several builds of a product can be listed (a rebuilt .build.info has no Active flag, and old
		// builds' configs stay behind after a patch); the first whose files are all stored here wins.
		// Unlisted builds in Data/config come last, as inactive.
		const candidates = [...products.filter((p) => p.product === product && p.active), ...products.filter((p) => p.product === product && !p.active)];
		if (!candidates.length) throw new Error(`Product ${product} is not installed`);
		const failures: string[] = [];
		for (const info of candidates) {
			try {
				return await CascStorage.openBuild(source, info, timings, time);
			} catch (e) {
				if (!(e instanceof NotLocalError)) throw e;
				failures.push(`build ${info.buildKey}: ${e.message}`);
			}
		}
		const message = `${failures.length === 1 ? 'The build' : `None of the ${failures.length} builds`} of ${product} can be read: ${failures.join('; ')}`;
		let report: string[];
		try {
			report = await describeStorage(source, await readBuildInfo(source));
		} catch (e) {
			report = [`Diagnostics failed: ${e instanceof Error ? e.message : String(e)}`];
		}
		throw new Error(`${message}\n\nDiagnostics:\n${report.join('\n')}`);
	}

	private static async openBuild(
		source: FileSource,
		info: ProductInfo,
		timings: Record<string, number>,
		time: <T>(label: string, fn: () => Promise<T>) => Promise<T>,
	): Promise<CascStorage> {
		const buildConfig = await readConfig(source, info.buildKey);

		const { index, names } = await time('Reading local indexes', () => loadLocalIndex(source));
		const archives = new ArchiveReader(source, index, names,new CdnReader(source,info));

		const [, encodingEKey] = requireConfig(buildConfig, 'encoding');
		const encoding = await time('Reading encoding table', async () =>
			EncodingTable.parse((await archives.read(fromHex(encodingEKey), 'encoding table')).data));

		const [rootCKey] = requireConfig(buildConfig, 'root');
        let rootSha256='';
		const root = await time('Reading root table', async () => {
			const ckey = fromHex(rootCKey);
			const ekeys = encoding.lookupAll(ckey);
			if (!ekeys.length) throw new Error('Root file missing from encoding table');
			// Normally one key; use the first that is stored here.
			const ekey = ekeys.find((key) => archives.index.find(key)) ?? ekeys[0];
			try {
				const bytes=(await archives.read(ekey, 'root table')).data;
                rootSha256=toHex(new Uint8Array(await crypto.subtle.digest('SHA-256',new Uint8Array(bytes))));
                return RootTable.parse(bytes);
			} catch (e) {
				if (!(e instanceof NotLocalError)) throw e;
				const elsewhere = ekeys.map((key) => archives.index.findInAnyBucket(key)).find(Boolean);
				throw new NotLocalError('root table', ` (content key ${rootCKey} has ${ekeys.length} encoding key(s) ${ekeys.map(toHex).join(' ')}; ` +
					`content key in index: ${archives.index.find(ckey) ? 'yes' : 'no'}; in another bucket: ${elsewhere ? `bucket ${elsewhere.bucket}` : 'no'}; ` +
					`index has ${archives.index.entryCount} entries in buckets ${archives.index.loadedBuckets})`);
			}
		});

		return new CascStorage(archives, encoding, root, {
			product: info.product,
            buildKey:info.buildKey,rootContentKey:rootCKey,rootSha256,readerVersion:'casc-root-evidence-2026-10-29.1',
			version: info.version,
			buildName: buildConfig.get('build-name')?.join(' ') ?? '',
			indexEntries: index.entryCount,
			encodingPages: encoding.pageCount,
			rootFiles: root.fileCount,
			rootNamedFiles: root.namedCount,
			timings,
		});
	}

	/** Calls back once if the game turns out to have been updated since the storage was opened. */
	onChanged(callback: () => void): void {
		this.archives.onChanged = callback;
	}

    inspectRoot(ids:readonly number[]) { return this.root.inspectFiles(ids); }

	/** Resolves a game path like "world/maps/azeroth/azeroth.wdt" via its root name hash. */
	lookupPath(path: string): number | null {
		const { hi, lo } = hashPath(path);
		return this.root.findByNameHash(hi, lo);
	}

	/**
	 * Picks the variant of a file to read: the first one stored locally, or failing that the
	 * best-known reason it can't be read.
	 */
	private resolve(fdid: number): { status: FileStatus; ekey?: Uint8Array; ckey?: Uint8Array } {
		const ckeys = this.root.getContentKeys(fdid);
		if (ckeys.length === 0) return { status: 'unknown' };
		let fallback: { status: FileStatus; ekey?: Uint8Array; ckey?: Uint8Array } = { status: 'no-encoding', ckey: ckeys[0] };
		for (const ckey of ckeys) {
            const keys=this.encoding.lookupAll(ckey);
            for(const ekey of keys){
                if(this.archives.index.find(ekey))return {status:'ok',ekey,ckey};
                fallback={status:'not-local',ekey,ckey};
            }
		}
		return fallback;
	}

	get remoteAvailable():boolean{return this.archives.remoteAvailable??false;}
	status(fdid: number): FileStatus {
		return this.resolve(fdid).status;
	}

	async readFile(fdid: number): Promise<Uint8Array> {
		return (await this.readFileWithStatus(fdid)).data;
	}

	/**
	 * With allowPartial, encrypted chunks are zero-filled instead of failing the read;
	 * encryptedKeys then lists the TACT keys that would be needed.
	 */
	async readFileWithStatus(fdid: number, allowPartial = false): Promise<BlteResult> {
		const { status, ekey, ckey } = this.resolve(fdid);
		if (status === 'unknown') throw new Error(`File ${fdid} is not in the root table`);
		if (!ekey) throw new Error(`File ${fdid} (${toHex(ckey!)}) is not in the encoding table`);
		const alternatives=this.root.getContentKeys(fdid).flatMap(c=>this.encoding.lookupAll(c));
        const ordered=[ekey,...alternatives.filter(k=>this.archives.index.find(k)),...alternatives];
        const tried=new Set<string>();let last:unknown;
        for(const candidate of ordered){
            const id=toHex(candidate);if(tried.has(id))continue;tried.add(id);
            try{return await this.archives.read(candidate,`File ${fdid}`,allowPartial);}
            catch(e){if(e instanceof StorageChangedError)throw e;last=e instanceof EncryptedError?e:(last??e);}
        }
        throw last;
	}
}

function requireConfig(config: Map<string, string[]>, key: string): string[] {
	const value = config.get(key);
	if (!value) throw new Error(`Build config has no ${key} entry`);
	return value;
}

/** The index, and the names of every index file in Data/data (sorted), to tell when an update replaces them. */
async function loadLocalIndex(source: FileSource): Promise<{ index: LocalIndex; names: string[] }> {
	// The newest index file per bucket is live, older ones are kept as a fallback (an install that
	// was patched may leave entries only in an older file).
	const files: { bucket: number; version: number; name: string }[] = [];
	const names: string[] = [];
	for (const name of await source.listDir(['Data', 'data'])) {
		const m = IDX_NAME.exec(name);
		if (m) names.push(name);
		if (!m) continue;
		const bucket = parseInt(m[1], 16);
		if (bucket < 16) files.push({ bucket, version: parseInt(m[2], 16), name });
	}
	if (files.length === 0) throw new Error('No local index files found in Data/data');
	files.sort((a, b) => b.version - a.version);

	const index = new LocalIndex();
	const bytes = await Promise.all(files.map(async ({ name }) => {
		const file = await source.openFile(['Data', 'data', name]);
		return file.read(0, file.size);
	}));
	const newest = new Set<number>();
	files.forEach(({ bucket, name }, i) => {
		try {
			index.addBucket(bucket, bytes[i], name);
			newest.add(bucket);
		} catch (e) {
			// An older file in a layout this doesn't read is only a lost fallback.
			if (!newest.has(bucket)) throw e;
		}
	});
	return { index, names: names.sort() };
}

/** Shortens a key for a report line: its first 8 hex digits. */
const short = (hex: string) => hex.slice(0, 8);

/**
 * What this storage holds, for when no build of a product could be read: the browser, the
 * listed builds, whether every archive the index points into is present, and for each build
 * config in Data/config which of its key files are stored here. The installed build has its
 * encoding, root and most of its encoding table's files stored; a leftover config doesn't.
 */
async function describeStorage(source: FileSource, products: ProductInfo[]): Promise<string[]> {
	const lines: string[] = [];
	if (typeof navigator !== 'undefined') lines.push(`Browser: ${navigator.userAgent}`);
	lines.push(`.build.info: ${products.map((p) => `${p.product} ${p.version || '(no version)'} ${short(p.buildKey)}${p.active ? ' active' : ''}`).join('; ')}`);

	const { index, names } = await loadLocalIndex(source);
	const archives = new ArchiveReader(source, index, names);
	lines.push(`Index files: ${index.names.length}, ${index.entryCount} entries`);

	const present = new Set<number>();
	for (const name of await source.listDir(['Data', 'data'])) {
		const m = /^data\.(\d{3})$/i.exec(name);
		if (m) present.add(Number(m[1]));
	}
	const referenced = [...index.archiveNumbers()].sort((a, b) => a - b);
	const missing = referenced.filter((n) => !present.has(n));
	const archiveName = (n: number) => `data.${n.toString().padStart(3, '0')}`;
	lines.push(`Archives: ${present.size} data.### files, index points into ${referenced.length} (${archiveName(referenced[0])} to ${archiveName(referenced[referenced.length - 1])})` +
		(missing.length ? `, missing ${missing.length}: ${missing.slice(0, 10).map(archiveName).join(' ')}` : ', none missing'));

	const stored = (ekey: string | undefined) => (ekey ? (index.find(fromHex(ekey)) ? 'yes' : 'no') : 'n/a');
	const configs = await listConfigKeys(source);
	let buildConfigs = 0;
	for (const key of configs) {
		let config: Map<string, string[]>;
		try {
			config = await readConfig(source, key);
		} catch (e) {
			lines.push(`Config ${short(key)}: unreadable (${e instanceof Error ? e.message : String(e)})`);
			continue;
		}
		// CDN configs list archives, not a root; only build configs matter here.
		if (!config.has('root')) continue;
		if (++buildConfigs > 12) {
			lines.push('(more build configs not checked)');
			break;
		}
		const uid = config.get('build-uid')?.[0] ?? '(no uid)';
		const name = config.get('build-name')?.join(' ') ?? '';
		const encodingEKey = config.get('encoding')?.[1];
		const parts = [`encoding ${stored(encodingEKey)}`];
		if (encodingEKey && index.find(fromHex(encodingEKey))) {
			try {
				const encoding = EncodingTable.parse((await archives.read(fromHex(encodingEKey), 'encoding table')).data);
				const rootEKeys = encoding.lookupAll(fromHex(config.get('root')![0]));
				parts.push(rootEKeys.length
					? `root ${rootEKeys.some((k) => index.find(k)) ? 'yes' : 'no'} (${rootEKeys.map((k) => short(toHex(k))).join(' ')})`
					: 'root not in its encoding table');
				const sample = encoding.sampleEncodingKeys(1000);
				parts.push(`${sample.filter((k) => index.find(k)).length} of ${sample.length} sampled files stored`);
			} catch (e) {
				parts.push(`encoding unreadable (${e instanceof Error ? e.message : String(e)})`);
			}
		}
		parts.push(`vfs-root ${stored(config.get('vfs-root')?.[1])}`, `install ${stored(config.get('install')?.[1])}`, `download ${stored(config.get('download')?.[1])}`);
		lines.push(`Config ${short(key)} ${uid} ${name}: ${parts.join(', ')}`);
	}
	lines.push(`Data/config: ${configs.length} files, ${buildConfigs} build configs`);
	return lines;
}

/** The keys of the config files in Data/config/xx/yy (build and CDN configs alike), lower case. */
async function listConfigKeys(source: FileSource): Promise<string[]> {
	const keys: string[] = [];
	for (const a of await listOrNone(source, ['Data', 'config'])) {
		for (const b of await listOrNone(source, ['Data', 'config', a])) {
			for (const name of await listOrNone(source, ['Data', 'config', a, b])) {
				if (/^[0-9a-f]{32}$/i.test(name)) keys.push(name.toLowerCase());
			}
		}
	}
	return keys;
}

async function listOrNone(source: FileSource, path: string[]): Promise<string[]> {
	try {
		return await source.listDir(path);
	} catch {
		return [];
	}
}
