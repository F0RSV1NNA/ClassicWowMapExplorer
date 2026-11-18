import { decodeText } from './reader';
import type { FileSource } from './source';

export interface ProductInfo {
	product: string;
	version: string;
	branch: string;
	active: boolean;
	buildKey: string;
	cdnKey: string;
    cdnPath?:string;
    cdnHosts?:string[];
}

/** Parses the pipe-separated .build.info table in the install root. */
export async function readBuildInfo(source: FileSource): Promise<ProductInfo[]> {
	const file = await source.openFile(['.build.info']);
	const lines = decodeText(await file.read(0, file.size)).split(/\r?\n/).filter((l) => l.trim());
	const columns = lines[0].split('|').map((c) => c.split('!')[0]);
	const col = (row: string[], name: string) => row[columns.indexOf(name)] ?? '';
	return lines.slice(1).map((line) => {
		const row = line.split('|');
		return {
			product: col(row, 'Product'),
			version: col(row, 'Version'),
			branch: col(row, 'Branch'),
			active: col(row, 'Active') === '1',
			buildKey: col(row, 'Build Key'),
			cdnKey: col(row, 'CDN Key'),
            cdnPath: col(row,'CDN Path'),
            cdnHosts: col(row,'CDN Hosts').split(' ').filter(Boolean),
		};
	});
}

/** Reads a "key = value value" config file from Data/config/xx/yy/<hash>. */
export async function readConfig(source: FileSource, hash: string): Promise<Map<string, string[]>> {
	const file = await source.openFile(['Data', 'config', hash.slice(0, 2), hash.slice(2, 4), hash]);
	const config = new Map<string, string[]>();
	for (const line of decodeText(await file.read(0, file.size)).split(/\r?\n/)) {
		if (!line || line.startsWith('#')) continue;
		const eq = line.indexOf('=');
		if (eq < 0) continue;
		config.set(line.slice(0, eq).trim(), line.slice(eq + 1).trim().split(/\s+/));
	}
	return config;
}
