import {expect,test}from 'bun:test';
import {RootTable}from '../src/casc/root';
const u32=(n:number)=>[n&255,n>>>8&255,n>>>16&255,n>>>24&255];
function root(entries:{id:number;alternate:boolean;key:number}[]){
 const bytes=[...u32(0x4d465354),...u32(24),...u32(1),...u32(entries.length),...u32(entries.length),0,0,0,0];
 for(const e of entries)bytes.push(...u32(1),...u32(e.alternate?0x80:0),...u32(2),...u32(e.id),...Array(16).fill(e.key),...Array(8).fill(0));
 return RootTable.parse(new Uint8Array(bytes));
}
test('alternate-only files remain addressable like CASCLib instead of becoming unknown',()=>{
 expect(root([{id:116306,alternate:true,key:8}]).getContentKeys(116306)).toEqual([new Uint8Array(16).fill(8)]);
});
test('ordinary records remain preferred when an alternate record also exists',()=>{
 const r=root([{id:116306,alternate:true,key:8},{id:116306,alternate:false,key:3}]);
 expect(r.getContentKeys(116306)).toEqual([new Uint8Array(16).fill(3)]);
});

test('modern v2 unnamed alternate records retain their file identities',()=>{
 const id=116306,b=[...u32(0x4d465354),...u32(24),...u32(2),...u32(1),...u32(0),0,0,0,0,
 ...u32(1),...u32(2),...u32(0x10000080),...u32(0),0,...u32(id),...Array(16).fill(7)];
 expect(RootTable.parse(new Uint8Array(b)).getContentKeys(id)).toEqual([new Uint8Array(16).fill(7)]);
});


test('unfiltered evidence distinguishes a locale-excluded ID from an absent ID',()=>{
 const bytes=[...u32(0x4d465354),...u32(24),...u32(1),...u32(2),...u32(2),0,0,0,0];
 for(const [id,locale] of [[116306,0x40],[116310,2]])bytes.push(...u32(1),...u32(0),...u32(locale),...u32(id),...Array(16).fill(7),...Array(8).fill(0));
 const r=RootTable.parse(new Uint8Array(bytes)),e=r.inspectFiles([116306,116310,999]);
 expect(r.getContentKeys(116306)).toEqual([]);
 expect(e.files[116306]).toEqual([{contentKey:'07'.repeat(16),contentFlags:'0x0',localeFlags:'0x40',selectedLocale:false}]);
 expect(e.files[116310][0].selectedLocale).toBe(true);
 expect(e.files[999]).toEqual([]);
 expect(e.parsedRecords).toBe(2);expect(e.declaredRecords).toBe(2);expect(e.blocks).toBe(2);
});

test('unfiltered evidence includes both ordinary and alternate records',()=>{
 const e=root([{id:116306,alternate:true,key:8},{id:116306,alternate:false,key:3}]).inspectFiles([116306]);
 expect(e.files[116306].map(r=>r.contentFlags)).toEqual(['0x80','0x0']);
});

test('unsupported and truncated manifests fail explicitly rather than produce a short catalog',()=>{
 expect(()=>RootTable.parse(new Uint8Array([...u32(0x4d465354),...u32(24),...u32(3),...u32(0),...u32(0),0,0,0,0]))).toThrow('Unsupported root version');
 expect(()=>RootTable.parse(new Uint8Array([...u32(0x4d465354),...u32(24),...u32(1),...u32(1),...u32(1),0,0,0,0,...u32(1),...u32(0),...u32(0x40)]))).toThrow('Truncated root block');
});
