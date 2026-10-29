import type { Viewer } from '../viewer/viewer';
import type { AsyncStorageApi } from '../worker/protocol';
import type { MeshInput, MeshPoint, MeshTile } from './types';
import { EditHistory, dab, editsForTile, editFingerprint, emptyEdits, newProject, readProject, type AuthoringProject, type BrushKind } from './authoring';
import { meshPositionDetails } from './position';
import { runRoadInspection, type RoadInspection } from './client';
import { writeExport } from './export';

export function setUpAuthoring(viewer:Viewer,storage:AsyncStorageApi,product:string,options:{busy:()=>boolean;directory:()=>FileSystemDirectoryHandle|null;changed:()=>void}) {
	const root=document.getElementById('mesh-authoring')!;
	root.innerHTML=`<div class="mesh-paint-toolbar"><button id="mesh-edit-undo" title="Ctrl+Z">Undo</button><button id="mesh-edit-redo" title="Ctrl+Shift+Z">Redo</button><button id="mesh-edit-save" class="mesh-primary">Save Edits</button></div><div class="mesh-tool-grid"><button data-paint-tool="off">Camera</button><button data-paint-tool="0" title="Mark this area as road">Road</button><button data-paint-tool="1" title="Clear road preference">Remove road</button><button data-paint-tool="3" title="Keep mesh visible but prevent walking here">Block</button><button data-paint-tool="2" title="Remove mesh in this area">Cut</button><button data-paint-tool="4" title="Restore automatic classification">Restore</button></div><label style="display:none">Cursor tool<select id="mesh-tool"><option value="off">Off — navigate camera</option><option value="0">Paint road</option><option value="1">Remove road</option><option value="3">Mark non-navigable</option><option value="2">Cut mesh</option><option value="4">Restore automatic surface</option><option value="link">Create connection (A then B)</option><option value="inspect">Inspect road textures</option></select></label>
	<p class="muted small">Drag to paint. Undo removes the last stroke. Escape returns to the camera.</p><label>Brush radius (yards)<input id="mesh-brush-radius" type="number" min="0.25" max="100" step="0.25" value="3"></label>
	<details><summary>Brush options</summary><label>Vertical reach ± yards<input id="mesh-brush-band" type="number" min="0.1" max="10" step="0.1" value="0.75"></label>
	<div class="muted small">Left-drag paints the selected surface. Escape disables the tool. Restore removes earlier paint/cuts at that spot on rebake. The vertical reach protects other floors.</div>
	<label><input id="mesh-edit-marks" type="checkbox" checked> Show painted areas</label></details>
	
	<button id="mesh-edit-rebake" class="mesh-primary">Apply edits / Rebake changed tiles</button><div id="mesh-edit-status" class="muted small" role="status" style="white-space:pre-line"></div>
	<details><summary>Files / Backups</summary><button id="mesh-edit-new">New / Clear edits</button><button id="mesh-edit-recover">Recover unsaved session</button><button id="mesh-edit-load">Load edits from open mesh folder</button>
	<button id="mesh-edit-download">Download edits file</button><label>Import edits file<input id="mesh-edit-import" type="file" accept=".json,application/json"></label>
	</details><details id="mesh-connections"><summary>Connections</summary>
    <button id="mesh-link-create" class="mesh-primary">New connection: click A, then B</button>
    <button id="mesh-link-cancel" hidden>Cancel placement</button>
    <div id="mesh-link-help" class="muted small" role="status">Choose New connection, then click two world surfaces.</div>
    <label>Connections in this map<select id="mesh-link-list" size="5" aria-label="Saved connections"></select></label>
    <label>Label<input id="mesh-link-note" maxlength="160" placeholder="e.g. Undercity east elevator"></label>
    <label>Type<select id="mesh-link-mode"><option value="walk">Walkable gap</option><option value="portal">Portal</option><option value="elevator">Elevator</option></select></label>
    <label><input id="mesh-link-both" type="checkbox" checked> Both directions (A to B and B to A)</label>
    <label>Attachment radius (yards)<input id="mesh-link-radius" type="number" min="0.1" max="20" step="0.1" value="2"></label>
    <div class="mesh-endpoint-a" id="mesh-link-a">A: not placed</div><button id="mesh-link-move-a">Move A: click a new surface</button>
    <div class="mesh-endpoint-b" id="mesh-link-b">B: not placed</div><button id="mesh-link-move-b">Move B: click a new surface</button>
    <div class="mesh-link-actions"><button id="mesh-link-update">Save changes</button><button id="mesh-link-delete">Delete connection</button></div>
    <div class="muted small">Blue = A. Orange = B. Placement uses the surface you click, without ground snapping. Save Edits writes your connections to a file; Apply edits adds them to the baked mesh. Portal/elevator execution still requires a runtime consumer.</div></details>
	<details><summary>Road texture rules</summary><button data-paint-tool="inspect">Inspect texture under cursor</button><div class="muted small">Choose Inspect road textures, then click terrain. All contributing layers are shown. Overrides affect this texture everywhere; rebake each affected map.</div>
	<select id="mesh-road-layers" aria-label="Terrain texture layers"></select><div id="mesh-road-info" class="muted small"></div>
	<button id="mesh-road-auto">Automatic</button><button id="mesh-road-on">Always road</button><button id="mesh-road-off">Never road</button></details>
	`;
	const el=<T extends HTMLElement>(id:string)=>document.getElementById(id) as T;
	const tool=el<HTMLSelectElement>('mesh-tool'),status=el('mesh-edit-status'),key=`mesh-authoring:${product}`;
	const project=newProject(product);
	let notice=localStorage.getItem(key)?'New edit session. Previous unsaved work is available under Files / Backups ? Recover unsaved session.':'New edit session. Use Save Edits to save a file.';
	const history=new EditHistory(project);
	let drawing:AuthoringProject|null=null,lastDab:MeshPoint|null=null,pending:MeshPoint|null=null,hover:MeshPoint|null=null;
	let inspection:RoadInspection|null=null,inspectionController:AbortController|null=null;
	let selectedLink:number|null=null,replacing:'A'|'B'|null=null;
	let saveQueue:Promise<void>=Promise.resolve(),mapKey='',lastBusy=false;
	const number=(id:string,min:number,max:number)=>{const n=el<HTMLInputElement>(id).valueAsNumber;if(!Number.isFinite(n)||n<min||n>max)throw Error(`Value must be between ${min} and ${max}.`);return n;};
	const currentMap=()=>viewer.meshPosition()?.mapId;
	const map=()=>history.project.maps[currentMap()??-1]??emptyEdits();
	const redraw=()=>{const id=currentMap();if(id!=null)viewer.showMeshEdits(id,el<HTMLInputElement>('mesh-edit-marks').checked?(drawing?.maps[id]?.Roads??map().Roads):[],el<HTMLInputElement>('mesh-edit-marks').checked?map().OffMesh:[],pending,selectedLink);};
	const report=(e:unknown)=>{notice=(e as Error).message;refresh();};
	const saveTo=async(directory:FileSystemDirectoryHandle)=>writeExport(directory,'mesh-edits.json',JSON.stringify(history.project,null,2));
	function changed(message:string){
		notice=message;
		try{localStorage.setItem(key,JSON.stringify(history.project));}catch(e){notice+=' Local save failed: '+(e as Error).message;}
		// Recovery drafts stay local. Only Save Edits or a bake writes a project file.
		options.changed();refresh(true);
	}
	function finish(){if(drawing){history.commit(drawing);drawing=null;lastDab=null;changed('Brush stroke saved. Rebake edited tiles to apply it.');}}
	function inspect(point:MeshPoint){
		inspectionController?.abort();const controller=inspectionController=new AbortController();notice='Reading terrain texture layers…';refresh();
		void (async()=>{
			const at=meshPositionDetails(point),tile=(await storage.meshTiles(point.mapId)).find(t=>t.x===at.col&&t.y===at.row);
			if(!tile)throw Error('No terrain tile at this point.');
			const input=await storage.meshSource(tile);controller.signal.throwIfAborted();input.edits=editsForTile(history.project,tile);
			inspection=await runRoadInspection(input,point.x,point.y,controller.signal);
			const list=el<HTMLSelectElement>('mesh-road-layers');list.replaceChildren();
			for(const layer of inspection.layers)list.add(new Option(`${layer.id} · ${Math.round(layer.weight/255*100)}% · ${layer.name??'unknown texture name'}`,String(layer.id)));
			notice=inspection.layers.length?'Texture layers loaded. Choose a layer and its rule.':'No texture data at this point.';
		})().catch(e=>{if(!controller.signal.aborted)notice=(e as Error).message;}).finally(()=>{if(inspectionController===controller)inspectionController=null;refresh();});
	}
	function pointer(phase:'down'|'move'|'up',point:MeshPoint|null){
		if(phase==='up'){finish();return;}
		if(options.busy()||inspectionController)return;
		hover=point;
		try{
            if(tool.value==='link'){
                viewer.showMeshConnectionCursor(point,replacing??(pending?'B':'A'));
                if(phase!=='down'||!point)return;
                if(replacing){
                    const id=currentMap(),next=structuredClone(history.project),link=id==null?null:next.maps[id]?.OffMesh.find(l=>l.UserId===selectedLink);
                    if(!link)throw Error('Select a connection first.');if(point.mapId!==id)throw Error('Endpoint must stay on this map.');
                    if(replacing==='A')Object.assign(link,{Ax:point.x,Ay:point.y,Az:point.z});else Object.assign(link,{Bx:point.x,By:point.y,Bz:point.z});
                    if(Math.hypot(link.Ax-link.Bx,link.Ay-link.By,link.Az-link.Bz)<.1)throw Error('Choose distinct endpoints.');
                    replacing=null;tool.value='off';history.commit(next);changed('Endpoint moved. Save Edits to save the connection.');return;
                }
                if(!pending){pending=point;notice='Blue A placed. Click the orange B endpoint.';redraw();refresh();return;}
                if(pending.mapId!==point.mapId)throw Error('Both endpoints must be on the same map.');
                if(Math.hypot(point.x-pending.x,point.y-pending.y,point.z-pending.z)<.1)throw Error('Choose distinct endpoints.');
                const next=structuredClone(history.project),m=next.maps[point.mapId]??=emptyEdits();
                selectedLink=m.OffMesh.reduce((max,l)=>Math.max(max,l.UserId),4095)+1;
                m.OffMesh.push({Ax:pending.x,Ay:pending.y,Az:pending.z,Bx:point.x,By:point.y,Bz:point.z,Radius:number('mesh-link-radius',.1,20),Bidirectional:el<HTMLInputElement>('mesh-link-both').checked,Mode:el<HTMLSelectElement>('mesh-link-mode').value as 'walk'|'portal'|'elevator',Note:el<HTMLInputElement>('mesh-link-note').value.trim()||'Connection '+selectedLink,UserId:selectedLink});
                pending=null;tool.value='off';history.commit(next);changed('Connection created. Edit its label below, then Save changes.');loadConnection();return;
            }
            const radius=number('mesh-brush-radius',.25,100),band=number('mesh-brush-band',.1,10);
            viewer.showMeshBrush(point,radius,tool.value==='2'?0xef633b:tool.value==='3'?0xf2338c:0xe9b949);
            if(!point)return;
            if(phase==='down'&&tool.value==='inspect'){inspect(point);return;}
			if(!['0','1','2','3','4'].includes(tool.value))return;
			if(phase==='down'){finish();drawing=structuredClone(history.project);lastDab=null;}
			if(!drawing)return;
			if(lastDab&&lastDab.mapId!==point.mapId){finish();return;}
			const distance=lastDab?Math.hypot(point.x-lastDab.x,point.y-lastDab.y):Infinity;
			if(distance<radius*.3)return;
			const m=drawing.maps[point.mapId]??=emptyEdits(),kind=Number(tool.value) as BrushKind;
			const count=lastDab&&distance<radius*10&&Math.abs(point.z-lastDab.z)<band*2?Math.min(100,Math.ceil(distance/(radius*.35))):1;
			for(let i=1;i<=count;i++){const t=i/count,p=lastDab&&count>1?{mapId:point.mapId,x:lastDab.x+(point.x-lastDab.x)*t,y:lastDab.y+(point.y-lastDab.y)*t,z:lastDab.z+(point.z-lastDab.z)*t}:point;m.Roads.push(dab(p,radius,band,kind));}
			lastDab=point;redraw();
		}catch(e){report(e);}
	}
	function refresh(force=false){
		const busy=options.busy()||!!inspectionController,id=currentMap(),signature=`${id}:${map().Roads.length}:${map().OffMesh.length}`;
		if(busy&&!lastBusy){finish();viewer.setMeshEditing(null);}
		lastBusy=busy;
		viewer.setMeshEditing(!busy&&tool.value!=='off'?pointer:null,tool.value==='link'?'surface':'ground');
		for(const node of root.querySelectorAll<HTMLInputElement|HTMLButtonElement|HTMLSelectElement>('input,button,select'))node.disabled=busy;
		el<HTMLButtonElement>('mesh-edit-undo').disabled=busy||!history.canUndo;el<HTMLButtonElement>('mesh-edit-redo').disabled=busy||!history.canRedo;
        if(force||signature!==mapKey){
            mapKey=signature;const list=el<HTMLSelectElement>('mesh-link-list');list.replaceChildren();
            for(const link of map().OffMesh)list.add(new Option((link.Note||'Connection '+link.UserId)+' | '+link.Mode+(link.Bidirectional?' | A <-> B':' | A -> B'),String(link.UserId)));
            if(!map().OffMesh.some(link=>link.UserId===selectedLink))selectedLink=null;
            list.value=selectedLink==null?'':String(selectedLink);redraw();
        }
        const selected=map().OffMesh.find(link=>link.UserId===selectedLink);
        const coordinates=(x:number,y:number,z:number)=>[x,y,z].map(n=>n.toFixed(2)).join(', ');
        el('mesh-link-a').textContent=selected?'A: '+coordinates(selected.Ax,selected.Ay,selected.Az):pending?'A: '+coordinates(pending.x,pending.y,pending.z):'A: not placed';
        el('mesh-link-b').textContent=selected?'B: '+coordinates(selected.Bx,selected.By,selected.Bz):'B: not placed';
        for(const name of ['update','delete','move-a','move-b'])el<HTMLButtonElement>('mesh-link-'+name).disabled=busy||!selected;
        el('mesh-link-cancel').hidden=tool.value!=='link';
        el('mesh-link-help').textContent=tool.value==='link'?(replacing?'Click the new '+replacing+' surface. Escape cancels.':pending?'A placed (blue). Click B (orange).':'Click A (blue), then B (orange). Escape cancels.'):selected?'Selected: '+(selected.Note||'Connection '+selected.UserId):map().OffMesh.length+' connections. Choose New connection or select one to edit.';

		for(const button of root.querySelectorAll<HTMLButtonElement>('[data-paint-tool]'))button.setAttribute('aria-pressed',String(button.dataset.paintTool===tool.value));
		const texture=Number(el<HTMLSelectElement>('mesh-road-layers').value),layer=inspection?.layers.find(l=>l.id===texture);
		el('mesh-road-info').textContent=layer?`Automatic: ${layer.automatic?'road':'not road'}; override: ${history.project.global.RoadOn.includes(texture)?'always road':history.project.global.RoadOff.includes(texture)?'never road':'automatic'}.`:'';
		status.textContent=notice;
	}
	for(const button of root.querySelectorAll<HTMLButtonElement>('[data-paint-tool]'))button.addEventListener('click',()=>{tool.value=button.dataset.paintTool!;tool.dispatchEvent(new Event('change'));});
	tool.addEventListener('change',()=>{finish();pending=null;replacing=null;refresh(true);});
	el('mesh-edit-marks').addEventListener('change',redraw);
	for(const id of ['mesh-brush-radius','mesh-brush-band'])el(id).addEventListener('change',()=>{if(tool.value==='link')return;if(hover)try{viewer.showMeshBrush(hover,number('mesh-brush-radius',.25,100),0xe9b949);}catch(e){report(e);}});
	window.addEventListener('keydown',e=>{if(e.code==='Escape'){finish();pending=null;replacing=null;tool.value='off';refresh(true);}});
	window.addEventListener('keydown',e=>{if(!e.ctrlKey&&!e.metaKey)return;if(e.target instanceof HTMLElement&&e.target.closest('input,textarea,select,[contenteditable]'))return;if(e.code==='KeyZ'||e.code==='KeyY'){e.preventDefault();if(!options.busy()){finish();if(e.code==='KeyY'||e.shiftKey)history.redo();else history.undo();pending=null;changed('History updated. Rebake edited tiles to apply.');}}});
	for(const action of ['undo','redo'] as const)el(`mesh-edit-${action}`).addEventListener('click',()=>{finish();history[action]();pending=null;changed(`${action==='undo'?'Undid':'Redid'} edit. Rebake edited tiles to update the mesh.`);});
    function loadConnection(){
        const link=map().OffMesh.find(l=>l.UserId===selectedLink);if(!link)return;
        el<HTMLSelectElement>('mesh-link-mode').value=link.Mode;el<HTMLInputElement>('mesh-link-note').value=link.Note;
        el<HTMLInputElement>('mesh-link-radius').value=String(link.Radius);el<HTMLInputElement>('mesh-link-both').checked=link.Bidirectional;
    }
    el('mesh-link-create').addEventListener('click',()=>{finish();pending=null;replacing=null;selectedLink=null;el<HTMLInputElement>('mesh-link-note').value='';tool.value='link';refresh(true);});
    el('mesh-link-cancel').addEventListener('click',()=>{pending=null;replacing=null;tool.value='off';refresh(true);});
    el('mesh-link-list').addEventListener('change',()=>{pending=null;replacing=null;tool.value='off';selectedLink=Number(el<HTMLSelectElement>('mesh-link-list').value)||null;loadConnection();refresh(true);});
    for(const endpoint of ['A','B'] as const)el('mesh-link-move-'+endpoint.toLowerCase()).addEventListener('click',()=>{finish();pending=null;replacing=endpoint;tool.value='link';refresh(true);});
    for(const action of ['update','delete'])el('mesh-link-'+action).addEventListener('click',()=>{try{
        const id=currentMap();if(id==null)return;const next=structuredClone(history.project),m=next.maps[id],link=m?.OffMesh.find(l=>l.UserId===selectedLink);
        if(!link)throw Error('Select a connection first.');
        if(action==='delete'){m.OffMesh=m.OffMesh.filter(l=>l!==link);selectedLink=null;}
        else Object.assign(link,{Mode:el<HTMLSelectElement>('mesh-link-mode').value,Note:el<HTMLInputElement>('mesh-link-note').value.trim(),Radius:number('mesh-link-radius',.1,20),Bidirectional:el<HTMLInputElement>('mesh-link-both').checked});
        pending=null;replacing=null;tool.value='off';history.commit(next);changed(action==='delete'?'Connection deleted. Undo restores it.':'Connection updated. Save Edits to save the file.');
    }catch(e){report(e);}});

    el('mesh-link-note').addEventListener('keydown',event=>{if(event.key==='Enter'){event.preventDefault();el('mesh-link-update').click();}});

	for(const mode of ['auto','on','off'])el(`mesh-road-${mode}`).addEventListener('click',()=>{const id=Number(el<HTMLSelectElement>('mesh-road-layers').value);if(!id){notice='Inspect terrain and select a texture first.';refresh();return;}const next=structuredClone(history.project);next.global.RoadOn=next.global.RoadOn.filter(v=>v!==id);next.global.RoadOff=next.global.RoadOff.filter(v=>v!==id);if(mode==='on')next.global.RoadOn.push(id);if(mode==='off')next.global.RoadOff.push(id);history.commit(next);changed(`Texture ${id}: ${mode}. This changes every map using the texture; rebake affected maps.`);});
	el('mesh-road-layers').addEventListener('change',()=>refresh());
	el('mesh-edit-new').addEventListener('click',()=>{finish();pending=null;history.commit(newProject(product));changed('New empty edit session. Undo restores the previous edits.');});
	el('mesh-edit-recover').addEventListener('click',()=>{try{const saved=localStorage.getItem(key);if(!saved)throw Error('No recovery draft is available.');finish();history.commit(readProject(saved,product));changed('Recovered previous unsaved edits. Use Save Edits to save a file.');}catch(e){report(e);}});
	el('mesh-edit-save').addEventListener('click',()=>{finish();const dir=options.directory();if(!dir){el('mesh-edit-download').click();notice='Edits downloaded as mesh-edits.json.';refresh();return;}void saveQueue.catch(()=>{}).then(()=>saveTo(dir)).then(()=>{notice='Saved mesh-edits.json to '+dir.name;refresh();}).catch(report);});
	el('mesh-edit-load').addEventListener('click',()=>{const dir=options.directory();if(!dir)return;void dir.getFileHandle('mesh-edits.json').then(h=>h.getFile()).then(f=>f.text()).then(text=>{history.commit(readProject(text,product));changed('Loaded edits from export. Undo restores the previous project.');}).catch(report);});
	el('mesh-edit-download').addEventListener('click',()=>{const url=URL.createObjectURL(new Blob([JSON.stringify(history.project,null,2)],{type:'application/json'})),a=document.createElement('a');a.href=url;a.download='mesh-edits.json';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);});
	el<HTMLInputElement>('mesh-edit-import').addEventListener('change',e=>{const file=(e.target as HTMLInputElement).files?.[0];if(file)void file.text().then(text=>{history.commit(readProject(text,product));changed('Imported edits. Undo restores the previous project.');}).catch(report);(e.target as HTMLInputElement).value='';});
	refresh(true);
	return {
		refresh,
		attach(input:MeshInput){input.edits=editsForTile(history.project,input.tile);},
		fingerprint(tile:MeshTile){return editFingerprint(editsForTile(history.project,tile));},
		async flush(){finish();await saveQueue;},saveTo,
		async openFolder(dir:FileSystemDirectoryHandle){
			if(Object.keys(history.project.maps).length||history.project.global.RoadOn.length||history.project.global.RoadOff.length)return;
			try{const text=await(await(await dir.getFileHandle('mesh-edits.json')).getFile()).text();history.commit(readProject(text,product));changed('Loaded the export’s saved edits.');}catch(e){if((e as Error).name!=='NotFoundError')report(e);}
		},
		async changedTiles(tiles:MeshTile[],dir:FileSystemDirectoryHandle){const result:MeshTile[]=[];for(const tile of tiles){let old='';try{const meta=JSON.parse(await(await(await dir.getFileHandle(`${tile.mapId}-${tile.x}-${tile.y}.json`)).getFile()).text());old=meta.authoringFingerprint??'';}catch(e){if(!['NotFoundError','SyntaxError'].includes((e as Error).name))throw e;}if(old!==editFingerprint(editsForTile(history.project,tile)))result.push(tile);}return result;},
	};
}
