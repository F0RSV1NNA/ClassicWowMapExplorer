import definitions from './definitions/LiquidType.dbd?raw';
import type { Db2 } from '../formats/db2';

/** Resolve the actual DB2 field by layout hash, as DBCD does; no names or ID heuristics. */
export function soundBankField(layoutHash: number | string): number {
	// Db2 exposes the unsigned numeric header hash; DBD layouts use eight hex digits.
	const hash = (typeof layoutHash === 'number' ? layoutHash.toString(16).padStart(8, '0') : layoutHash).toUpperCase();
	let matches = false, field = 0;
	for (const raw of definitions.split(/\r?\n/)) {
		const line = raw.trim();
		if (line.startsWith('LAYOUT ')) { matches = line.slice(7).split(',').map((s) => s.trim()).includes(hash); field = 0; continue; }
		if (!matches || !line || line.startsWith('BUILD ') || line.startsWith('//')) continue;
		if (line.includes('$noninline')) continue;
		if (/^SoundBank\b/.test(line)) return field;
		field++;
	}
	throw new Error(`LiquidType layout ${layoutHash} has no bundled SoundBank definition. Update WoWDBDefs; refusing to guess liquid safety.`);
}

export function classifyLiquid(table: Db2, type: number): number {
	const bank = table.getInt(type, soundBankField(table.layoutHash));
	if (bank == null || bank < 0 || bank > 3) throw new Error(`LiquidType ${type} has unsupported SoundBank ${bank}`);
	return bank + 1; // Mappster LiquidClass 0..3 -> browser input kinds 1..4.
}
