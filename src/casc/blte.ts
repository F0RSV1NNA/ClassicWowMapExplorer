import keyCatalog from './tact-keys.json';
import { salsa20 } from './salsa20';
import { unzlibSync } from 'fflate';
import { Reader, concatBytes, fromHex, toHex } from './reader';

const BLTE_MAGIC = 0x424c5445;

export class EncryptedError extends Error {
	constructor(readonly keyName: string) {
		super(`Encrypted with TACT key ${keyName}`);
		this.name = 'EncryptedError';
	}
}

export interface BlteResult {
	data: Uint8Array;
	/** TACT key names of chunks that were zero-filled because they are encrypted. */
	encryptedKeys: string[];
}

interface ChunkInfo {
	compressedSize: number;
	decompressedSize: number;
}

/**
 * Decodes a BLTE stream. Encrypted chunks throw EncryptedError, unless allowPartial is set,
 * in which case they are zero-filled and reported in encryptedKeys.
 */
export function decodeBlte(input: Uint8Array, allowPartial = false): BlteResult {
	const r = new Reader(input);
	if (input.length < 8 || r.u32be() !== BLTE_MAGIC) {
		const head = toHex(input.subarray(0, 16));
		const text = String.fromCharCode(...input.subarray(0, 16)).replace(/[^\x20-\x7e]/g, '.');
		throw new Error(`Not a BLTE stream (${input.length} bytes, starts with ${head || 'nothing'} "${text}")`);
	}
	const headerSize = r.u32be();

	const chunks: ChunkInfo[] = [];
	if (headerSize === 0) {
		chunks.push({ compressedSize: input.length - 8, decompressedSize: -1 });
	} else {
		r.u8(); // flags
		const count = r.u24be();
		for (let i = 0; i < count; i++) {
			const compressedSize = r.u32be();
			const decompressedSize = r.u32be();
			r.skip(16); // md5
			chunks.push({ compressedSize, decompressedSize });
		}
	}

	let offset = headerSize === 0 ? 8 : headerSize;
	const parts: Uint8Array[] = [];
	const encryptedKeys: string[] = [];
	for (let index=0;index<chunks.length;index++) {
        const chunk=chunks[index];
		const block = input.subarray(offset, offset + chunk.compressedSize);
		offset += chunk.compressedSize;
        if(offset>input.length)throw Error('Truncated BLTE chunk');
        const part=decodeChunk(block,chunk.decompressedSize,allowPartial,encryptedKeys,index);
        if(chunk.decompressedSize>=0&&part.length!==chunk.decompressedSize)throw Error('BLTE decompressed size mismatch');
        parts.push(part);
	}
	return { data: concatBytes(parts), encryptedKeys };
}

function decodeChunk(block: Uint8Array, decompressedSize: number, allowPartial: boolean, encryptedKeys: string[],index=0,depth=0): Uint8Array {
    if(depth>16||!block.length)throw Error('Invalid BLTE chunk nesting or empty block');
	switch (block[0]) {
		case 0x4e: // 'N' raw
			return block.subarray(1);
		case 0x5a: // 'Z' zlib
			return decompressedSize > 0
				? unzlibSync(block.subarray(1), { out: new Uint8Array(decompressedSize) })
				: unzlibSync(block.subarray(1));
		case 0x45: { // 'E' encrypted
			const keyNameSize = block[1];
			// Key names are stored little-endian; display them the way key lists write them.
			const keyName = toHex(block.slice(2, 2 + keyNameSize).reverse()).toUpperCase();
            const known=(keyCatalog as Record<string,string>)[keyName];
            if(!known){
                if(!allowPartial||decompressedSize<0)throw new EncryptedError(keyName);
                encryptedKeys.push(keyName);return new Uint8Array(decompressedSize);
            }
            if(keyNameSize!==8||block.length<12)throw Error('Invalid BLTE encryption header');
            const ivSize=block[10],typeAt=11+ivSize;
            if((ivSize!==4&&ivSize!==8)||typeAt>=block.length)throw Error('Invalid BLTE encryption IV');
            if(block[typeAt]!==0x53)throw Error('Unsupported BLTE encryption type');
            const nonce=new Uint8Array(8);nonce.set(block.subarray(11,typeAt));
            for(let i=0;i<4;i++)nonce[i]^=index>>>(i*8)&255;
            const plain=salsa20(block.subarray(typeAt+1),fromHex(known),nonce);
            return decodeChunk(plain,decompressedSize,allowPartial,encryptedKeys,index,depth+1);
		}
		case 0x46: // 'F' nested frame
			{const nested=decodeBlte(block.subarray(1),allowPartial);encryptedKeys.push(...nested.encryptedKeys);return nested.data;}
		default:
			throw new Error(`Unknown BLTE chunk mode 0x${block[0].toString(16)}`);
	}
}
