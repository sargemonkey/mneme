'use strict';
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('mneme', {
    config:        ()    => ipcRenderer.invoke('mneme:config'),
    metrics:       ()    => ipcRenderer.invoke('mneme:metrics'),
    events:        (n)   => ipcRenderer.invoke('mneme:events', n),
    curations:     (n)   => ipcRenderer.invoke('mneme:curations', n),
    workstreams:   ()    => ipcRenderer.invoke('mneme:workstreams'),
    setWorkstream: (ws)  => ipcRenderer.invoke('mneme:setWorkstream', ws),
    pickDatabase:  ()    => ipcRenderer.invoke('mneme:pickDatabase'),
    cli:           (args) => ipcRenderer.invoke('mneme:cli', args),
    onChange:      (cb)  => ipcRenderer.on('mneme:changed', cb),
});
