import { Alert, Badge, Button, Checkbox, Drawer, Group, Image, Loader, NumberInput, Pagination, Paper, Select, SimpleGrid, Stack, Switch, Table, Tabs, Text, TextInput, Title } from '@mantine/core'
import { IconHeartbeat, IconRefresh, IconScan, IconTrash } from '@tabler/icons-react'
import { useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { useImageCache, useRebuildImageCache } from '../api/hooks'
import { useHealthAction, useHealthData, type FileDetail, type HealthFile, type HealthOperation, type HealthOptions, type HealthOverview, type OperationDetail } from '../api/health'

const color = (status: string) => status === 'error' || status === 'failed' ? 'red' : ['warning', 'partial', 'open'].includes(status) ? 'yellow' : ['healthy', 'complete', 'completed'].includes(status) ? 'green' : 'gray'
const bytes = (size: number) => size < 0 ? 'Missing' : `${(size / 1024 / 1024).toFixed(1)} MiB`
function Status({ value }: { value: string }) { return <Badge color={color(value)} variant="light">{value}</Badge> }

export default function HealthPage() {
  const [params, setParams] = useSearchParams()
  const tab = params.get('tab') ?? 'overview'
  const overview = useHealthData<HealthOverview>()
  const [fileId, setFileId] = useState<number | null>(null)
  const [operationId, setOperationId] = useState<number | null>(null)
  const [search, setSearch] = useState('')
  const [root, setRoot] = useState<string | null>(null)
  const [kind, setKind] = useState<string | null>(null)
  const [state, setState] = useState<string | null>('open')
  const [page, setPage] = useState(1)
  const [historyPage, setHistoryPage] = useState(1)
  const [repairPage, setRepairPage] = useState(1)
  const query = new URLSearchParams({page: String(page), search})
  if (root) query.set('rootId', root)
  if (kind) query.set('kind', kind)
  if (state) query.set('state', state)
  const files = useHealthData<{items: HealthFile[]; total: number}>(`/files?${query}`, tab === 'files')
  const operations = useHealthData<{items: HealthOperation[]; total: number}>(`/operations?page=${repairPage}`, tab === 'repairs')
  const history = useHealthData<{items: {id: number; createdAt: string; kind: string; message: string}[]; total: number}>(`/history?page=${historyPage}`, tab === 'history')
  const action = useHealthAction()
  const run = (path: string, body = {}) => action.mutate({path, body})
  const error = overview.error ?? files.error ?? operations.error ?? history.error ?? action.error
  return <Stack gap="lg">
    <Group justify="space-between"><div><Group gap="xs"><IconHeartbeat /><Title order={2}>Health</Title></Group><Text c="dimmed">System checks and reviewed library maintenance</Text></div><Group><Button variant="default" leftSection={<IconRefresh size={16}/>} loading={action.isPending} onClick={() => run('/refresh')}>Check now</Button><Button leftSection={<IconScan size={16}/>} onClick={() => run('/scans', {rootFolderId: root ? Number(root) : null})}>Scan files</Button></Group></Group>
    {error && <Alert color="red">{error.message}</Alert>}
    {overview.isPending && <Loader/>}
    <SimpleGrid cols={{base: 1, sm: 3}}>{[['System issues', overview.data?.checks.filter(c => ['warning','error','unavailable'].includes(c.status)).length ?? 0], ['Open file findings', overview.data?.openFindings ?? 0], ['Archives inventoried', overview.data?.files ?? 0]].map(([label, value]) => <Paper withBorder p="md" key={label}><Text size="sm" c="dimmed">{label}</Text><Text size="xl" fw={700}>{value}</Text></Paper>)}</SimpleGrid>
    {overview.data?.scans.filter(s => ['pending','running'].includes(s.status)).map(scan => <Alert key={scan.id} title={`Scan ${scan.id}: ${scan.status}`}><Group justify="space-between"><Text>{scan.completed} / {scan.total} files inspected</Text><Button size="xs" variant="default" onClick={() => run(`/scans/${scan.id}/cancel`)}>Cancel scan</Button></Group></Alert>)}
    {overview.data?.scans.find(s => s.error) && <Alert color="yellow">Last partial scan: {overview.data.scans.find(s => s.error)?.error}</Alert>}
    <Tabs value={tab} onChange={value => setParams({tab: value ?? 'overview'})}>
      <Tabs.List><Tabs.Tab value="overview">Overview</Tabs.Tab><Tabs.Tab value="files">Files</Tabs.Tab><Tabs.Tab value="repairs">Repairs</Tabs.Tab><Tabs.Tab value="history">History</Tabs.Tab></Tabs.List>
      <Tabs.Panel value="overview" pt="lg"><Stack>
        {overview.data?.checks.length === 0 && <Alert>No checks have run yet. Use Check now.</Alert>}
        {Array.from(new Set(overview.data?.checks.map(c => c.category))).map(category => <Paper key={category} withBorder p="md"><Title order={4} tt="capitalize" mb="sm">{category}</Title><Stack gap="sm">{overview.data?.checks.filter(c => c.category === category).map(check => <Group key={check.id} justify="space-between" align="flex-start"><div><Group gap="xs"><Status value={check.status}/><Text size="sm">{check.message}</Text></Group><Text size="xs" c="dimmed" mt={4}>{new Date(check.checkedAt).toLocaleString()}{check.acknowledged ? ' · Acknowledged' : ''}</Text></div><Group gap="xs">{check.url && <Button component={Link} to={check.url} size="xs" variant="subtle">Open</Button>}{['error','warning','unavailable'].includes(check.status) && <Button size="xs" variant="subtle" onClick={() => run('/checks/acknowledge', {id: check.id, acknowledged: !check.acknowledged})}>{check.acknowledged ? 'Reopen' : 'Acknowledge'}</Button>}</Group></Group>)}</Stack></Paper>)}
        <CachePanel/>
        <OptionsPanel/>
      </Stack></Tabs.Panel>
      <Tabs.Panel value="files" pt="lg"><Stack>
        <Group><TextInput placeholder="Search file paths" aria-label="Search file paths" value={search} onChange={e => {setSearch(e.currentTarget.value); setPage(1)}}/><Select placeholder="All roots" clearable value={root} onChange={v => {setRoot(v); setPage(1)}} data={overview.data?.roots.map(r => ({value: String(r.id), label: r.path})) ?? []}/><Select placeholder="All findings" clearable value={kind} onChange={v => {setKind(v); setPage(1)}} data={['missing','empty','corrupt','noPages','damagedImage','duplicate','pageRepetition','blankRepetition','unlinked','sizeMismatch','incomplete']}/><Select placeholder="All states" clearable value={state} onChange={v => {setState(v); setPage(1)}} data={['open','acknowledged','ignored','resolved']}/></Group>
        {files.isPending ? <Loader/> : <><Table.ScrollContainer minWidth={650}><Table striped highlightOnHover><Table.Thead><Table.Tr><Table.Th>Archive</Table.Th><Table.Th>Size</Table.Th><Table.Th>Analysis</Table.Th><Table.Th>Findings</Table.Th><Table.Th/></Table.Tr></Table.Thead><Table.Tbody>{files.data?.items.map(file => <Table.Tr key={file.id}><Table.Td><Text size="sm" style={{overflowWrap:'anywhere'}}>{file.relativePath}</Text><Text size="xs" c="dimmed">{file.analyzedAt ? new Date(file.analyzedAt).toLocaleString() : 'Not analyzed'}</Text></Table.Td><Table.Td>{bytes(file.size)}</Table.Td><Table.Td><Status value={file.status}/></Table.Td><Table.Td><Group gap={4}>{file.findings.map(f => <Badge key={f.id} color={color(f.severity)} variant="light">{f.kind}</Badge>)}</Group></Table.Td><Table.Td><Button size="xs" variant="default" onClick={() => setFileId(file.id)}>Review</Button></Table.Td></Table.Tr>)}</Table.Tbody></Table></Table.ScrollContainer>{files.data?.items.length === 0 && <Text c="dimmed">No files match these filters. Run a scan to inventory the library.</Text>}<Pagination value={page} onChange={setPage} total={Math.max(1, Math.ceil((files.data?.total ?? 0)/30))}/></>}
      </Stack></Tabs.Panel>
      <Tabs.Panel value="repairs" pt="lg"><Stack>{operations.data?.items.map(op => <Paper withBorder p="md" key={op.id}><Group justify="space-between"><div><Group><Text fw={600}>{op.kind === 'delete' ? 'Deletion review' : 'Replacement'} #{op.id}</Text><Status value={op.status}/></Group><Text size="sm" c="dimmed">{op.error ?? new Date(op.createdAt).toLocaleString()}</Text></div><Button variant="default" onClick={() => setOperationId(op.id)}>Review</Button></Group></Paper>)}{operations.data?.items.length === 0 && <Text c="dimmed">No repairs or deletions requested.</Text>}<Pagination value={repairPage} onChange={setRepairPage} total={Math.max(1, Math.ceil((operations.data?.total ?? 0)/30))}/></Stack></Tabs.Panel>
      <Tabs.Panel value="history" pt="lg"><Stack>{history.data?.items.map(entry => <Group key={entry.id} align="flex-start"><Badge variant="light">{entry.kind}</Badge><div><Text size="sm">{entry.message}</Text><Text size="xs" c="dimmed">{new Date(entry.createdAt).toLocaleString()}</Text></div></Group>)}<Pagination value={historyPage} onChange={setHistoryPage} total={Math.max(1, Math.ceil((history.data?.total ?? 0)/30))}/></Stack></Tabs.Panel>
    </Tabs>
    {fileId != null && <FileReview key={fileId} id={fileId} close={() => setFileId(null)} openOperation={id => {setOperationId(id); setFileId(null)}}/>}
    {operationId != null && <OperationReview key={operationId} id={operationId} close={() => setOperationId(null)}/>}
  </Stack>
}

function FileReview({id, close, openOperation}: {id: number; close: () => void; openOperation: (id: number) => void}) {
  const {data, error} = useHealthData<FileDetail>(`/files/${id}`)
  const action = useHealthAction()
  const [mapping, setMapping] = useState<string | null>(null)
  const [evidencePage, setEvidencePage] = useState(1)
  return <Drawer opened onClose={close} title="Review archive" size="xl"><Stack>
    {(error ?? action.error) && <Alert color="red">{(error ?? action.error)?.message}</Alert>}
    {!data ? <Loader/> : <><Text fw={600} style={{overflowWrap:'anywhere'}}>{data.file.relativePath}</Text><Text size="sm">{bytes(data.file.size)} · {data.analysis.pages.length} pages · {data.analysis.status}</Text><Text size="xs" c="dimmed" style={{overflowWrap:'anywhere'}}>SHA-256: {data.file.contentHash ?? 'Unavailable'}</Text>
    <Group>{data.file.seriesId && <Button component={Link} to={`/series/${data.file.seriesId}`} variant="default">Open series</Button>}{!data.file.chapterFileId && <Button component={Link} to="/import" variant="default">Import archive</Button>}<Button variant="default" onClick={() => action.mutate({path:'/scans', body:{fileIds:[id], force:true}})}>Rescan</Button></Group>
    <Text size="sm">Affected chapters: {data.chapters.map(c => c.number ?? c.title ?? c.id).join(', ') || 'None linked'}</Text>
    {data.findings.map(f => <Paper key={f.id} withBorder p="sm"><Group><Status value={f.severity}/><Text size="sm">{f.message}</Text></Group><Group mt="xs"><Text size="xs" c="dimmed">{f.state}</Text>{['acknowledged','ignored','open'].filter(s => s !== f.state).map(state => <Button key={state} size="xs" variant="subtle" onClick={() => action.mutate({path:`/findings/${f.id}`, method:'PUT', body:{version:f.version, state}})}>{state === 'open' ? 'Reopen' : state === 'ignored' ? 'Ignore this version' : 'Acknowledge'}</Button>)}</Group></Paper>)}
    {data.analysis.repetitions.length > 0 && <><Alert color="yellow">Similar pages can be intentional. Review before requesting a replacement.</Alert>{data.analysis.repetitions.slice((evidencePage-1)*5,evidencePage*5).map(pair => <Paper withBorder p="sm" key={`${pair.first}-${pair.second}`}><Text size="sm" mb="sm">Pages {pair.first+1} and {pair.second+1}: {pair.kind}{pair.kind === 'similar' ? ` (distance ${pair.distance})` : ''}</Text><SimpleGrid cols={2}>{[pair.first,pair.second].map(page => <Image key={page} src={`/api/v1/health/files/${id}/pages/${page}?version=${data.file.version}`} alt={`Page ${page+1}`} h={280} fit="contain"/>)}</SimpleGrid></Paper>)}<Pagination value={evidencePage} onChange={setEvidencePage} total={Math.ceil(data.analysis.repetitions.length/5)}/></>}
    <Title order={4}>Request replacement</Title><Select placeholder="Choose mapped source" value={mapping} onChange={setMapping} data={data.mappings.map(m => ({value:String(m.id), label:m.sourceName}))}/><Button disabled={!mapping || data.chapters.length === 0} loading={action.isPending} onClick={() => action.mutate({path:'/repairs', body:{fileId:id, version:data.file.version, sourceMappingId:Number(mapping)}}, {onSuccess: op => openOperation(op.id)})}>Download candidate for review</Button><Text size="xs" c="dimmed">All chapters sharing this archive must have a candidate. The original stays in place until you approve application.</Text>
    <Button color="red" variant="light" leftSection={<IconTrash size={16}/>} onClick={() => action.mutate({path:'/deletions/preview', body:{fileId:id, version:data.file.version}}, {onSuccess: op => openOperation(op.id)})}>Review permanent deletion</Button></>}
  </Stack></Drawer>
}

function OperationReview({id, close}: {id:number; close:()=>void}) {
  const {data,error} = useHealthData<OperationDetail>(`/operations/${id}`)
  const action = useHealthAction()
  const [confirm,setConfirm] = useState(false)
  const [reset,setReset] = useState(false)
  return <Drawer opened onClose={close} title={`Operation #${id}`} size="xl"><Stack>
    {(error ?? action.error) && <Alert color="red">{(error ?? action.error)?.message}</Alert>}
    {!data ? <Loader/> : <><Status value={data.operation.status}/><Text fw={600}>{data.file.relativePath}</Text><Text>{bytes(data.file.size)} · {data.chapters.length} affected chapters</Text>{data.operation.error && <Alert color="red">{data.operation.error}</Alert>}
    {data.operation.kind === 'delete' ? <Alert color="red">This permanently deletes the archive. Chapter records and reading history remain. {data.chapters.some(c => c.wanted) ? 'Wanted chapters may be downloaded again by existing automation.' : ''}</Alert> : <><Text c="dimmed">Review every candidate before applying. Originals remain unchanged until approval.</Text>{data.candidates.map(candidate => <Paper key={candidate.chapterId} withBorder p="sm"><Text fw={600}>Chapter {candidate.chapterId}: {candidate.analysis.pages.length} pages</Text><Status value={candidate.analysis.status}/>{candidate.analysis.problems.map((p,i) => <Text key={i} c={color(p.severity)} size="sm">{p.message}</Text>)}{candidate.analysis.repetitions.length > 0 && <Alert color="yellow">{candidate.analysis.repetitions.length} repeated or similar page pairs</Alert>}<SimpleGrid cols={2}>{candidate.analysis.pages.slice(0,4).map((p,index) => <Image key={p.name} h={200} fit="contain" src={`/api/v1/health/operations/${id}/candidates/${candidate.chapterId}/pages/${index}`} alt={`Candidate page ${index+1}`}/>)}</SimpleGrid></Paper>)}{data.requiresReset && <Checkbox checked={reset} onChange={e => setReset(e.currentTarget.checked)} label="Reset bookmarks and resume positions for affected chapters across all users. Completed status and reading history are preserved."/>}</>}
    {data.operation.status === 'review' && <><Checkbox checked={confirm} onChange={e => setConfirm(e.currentTarget.checked)} label={data.operation.kind === 'delete' ? 'I confirm permanent deletion of this archive and all its file links.' : 'I approve applying these replacement files.'}/><Button color={data.operation.kind === 'delete' ? 'red' : 'brand'} loading={action.isPending} disabled={!confirm || (data.operation.kind === 'repair' && data.requiresReset && !reset)} onClick={() => action.mutate({path:`/operations/${id}/apply`,body:{version:data.operation.version,confirmed:confirm,resetPositions:reset}}, {onSuccess:close})}>{data.operation.kind === 'delete' ? 'Permanently delete' : 'Apply replacement'}</Button></>}
    {!['completed','cancelled','failed'].includes(data.operation.status) && <Button variant="default" onClick={() => action.mutate({path:`/operations/${id}/cancel`}, {onSuccess:close})}>Cancel operation</Button>}</>}
  </Stack></Drawer>
}

function OptionsPanel() {
  const {data} = useHealthData<HealthOptions>('/options')
  const [draft,setDraft] = useState<HealthOptions | null>(null)
  const action = useHealthAction()
  const value = draft ?? data
  if (!value) return null
  const update = (patch: Partial<HealthOptions>) => setDraft({...value,...patch})
  return <Paper withBorder p="md"><Stack><Title order={4}>Health settings</Title>{action.error && <Alert color="red">{action.error.message}</Alert>}<Switch label="Analyze new files and run daily reconciliation" checked={value.automaticScanning} onChange={e => update({automaticScanning:e.currentTarget.checked})}/><SimpleGrid cols={{base:1,sm:2}}><NumberInput label="Daily scan hour (0–23)" min={0} max={23} value={value.scanHour} onChange={v => update({scanHour:Number(v)})}/><TextInput label="Timezone (empty uses server timezone)" value={value.timeZone ?? ''} onChange={e => update({timeZone:e.currentTarget.value || null})}/>{(['warningPercent','errorPercent','warningGiB','errorGiB','backupDays'] as const).map(key => <NumberInput key={key} label={{warningPercent:'Low disk warning (%)',errorPercent:'Low disk error (%)',warningGiB:'Low disk warning (GiB)',errorGiB:'Low disk error (GiB)',backupDays:'Backup freshness (days)'}[key]} min={0} value={value[key]} onChange={v => update({[key]:Number(v)})}/>)}</SimpleGrid><Button disabled={!draft} loading={action.isPending} onClick={() => action.mutate({path:'/options',method:'PUT',body:value},{onSuccess:()=>setDraft(null)})}>Save health settings</Button></Stack></Paper>
}

function CachePanel() {
  const cache = useImageCache()
  const rebuild = useRebuildImageCache()
  return <Paper withBorder p="md"><Stack><Title order={4}>Image cache and backups</Title>
    {cache.data && <Text size="sm">Posters: {bytes(cache.data.usage.coverBytes)} · Thumbnails: {bytes(cache.data.usage.thumbnailBytes)} · Missing posters: {cache.data.usage.coversMissing}</Text>}
    {(cache.error ?? rebuild.error) && <Alert color="red">{(cache.error ?? rebuild.error)?.message}</Alert>}
    <Group><Button variant="default" loading={rebuild.isPending || cache.data?.status.running} onClick={() => rebuild.mutate(false)}>Rebuild missing images</Button><Button component={Link} to="/settings?tab=system" variant="subtle">Backup and cache tools</Button></Group>
  </Stack></Paper>
}
