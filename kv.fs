﻿(*
    Copyright 2015 Zumero, LLC

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <http://www.gnu.org/licenses/>.
*)

namespace Zumero

type index_info =
    {
        db:string
        coll:string
        ndx:string
        spec:BsonValue
        options:BsonValue
    }

type tx =
    {
        database:string
        collection:string
        insert:BsonValue->unit
        update:BsonValue->unit
        delete:BsonValue->bool
        getSelect:unit->(seq<BsonValue>*(unit->unit))
        commit:unit->unit
        rollback:unit->unit
    }

type conn_methods =
    {
        listCollections:unit->(string*string*BsonValue)[]
        createCollection:string->string->BsonValue->bool
        dropCollection:string->string->bool
        renameCollection:string->string->bool->bool
        clearCollection:string->string->bool
        listIndexes:unit->index_info[]
        createIndexes:index_info[]->bool[]
        dropIndex:string->string->string->bool
        beginWrite:string->string->tx
        getCollectionSequence:string->string->(seq<BsonValue>*(unit->unit))
        dropDatabase:string->bool
        close:unit->unit
    }

module kv =
    open SQLitePCL
    open SQLitePCL.Ugly

    let private encodeIndexKey ndxInfo doc =
        let w = BinWriter()
        match ndxInfo.spec with
        | BDocument keys ->
            Array.iter (fun (k,dir) ->
                let v = 
                    // TODO maybe when a key is missing we should just not insert an index entry for this doc?
                    match bson.findPath doc k with
                    | Some v -> v
                    | None -> BUndefined
                bson.encodeForIndexInto w v
            ) keys
        | _ -> failwith "must be a doc"
        w.ToArray()

    // TODO special type for the pair db and coll

    let connect() =
        let conn = ugly.``open``("elmodata.db")
        conn.exec("PRAGMA journal_mode=WAL")
        conn.exec("PRAGMA foreign_keys=ON")
        conn.exec("CREATE TABLE IF NOT EXISTS \"collections\" (dbName TEXT NOT NULL, collName TEXT NOT NULL, options BLOB NOT NULL, PRIMARY KEY (dbName,collName))")
        conn.exec("CREATE TABLE IF NOT EXISTS \"indexes\" (dbName TEXT NOT NULL, collName TEXT NOT NULL, ndxName TEXT NOT NULL, spec BLOB NOT NULL, options BLOB NOT NULL, PRIMARY KEY (dbName, collName, ndxName), FOREIGN KEY (dbName,collName) REFERENCES \"collections\" ON DELETE CASCADE ON UPDATE CASCADE, UNIQUE (spec,dbName,collName))")

        let getCollectionOptions db coll =
            use stmt = conn.prepare("SELECT options FROM \"collections\" WHERE dbName=? AND collName=?")
            stmt.bind_text(1,db)
            stmt.bind_text(2,coll)
            if raw.SQLITE_ROW=stmt.step() then
                stmt.column_blob(0) |> BinReader.ReadDocument |> Some
            else
                None

        let sqlSelectIndexes = "SELECT ndxName, spec, options, dbName, collName FROM \"indexes\""

        let getIndexFromRow (stmt:sqlite3_stmt) =
            let ndx = stmt.column_text(0)
            let spec = stmt.column_blob(1) |> BinReader.ReadDocument
            let options = stmt.column_blob(2) |> BinReader.ReadDocument
            let db = stmt.column_text(3)
            let coll = stmt.column_text(4)
            {db=db;coll=coll;ndx=ndx;spec=spec;options=options}

        let getIndexInfo db coll ndx =
            use stmt = conn.prepare(sqlSelectIndexes + " WHERE dbName=? AND collName=? AND ndxName=?")
            stmt.bind_text(1,db)
            stmt.bind_text(2,coll)
            stmt.bind_text(3,ndx)
            if raw.SQLITE_ROW=stmt.step() then
                getIndexFromRow(stmt) |> Some
            else
                None

        let getTableNameForCollection db coll = sprintf "docs.%s.%s" db coll // TODO cleanse?

        let getTableNameForIndex db coll ndx = sprintf "ndx.%s.%s.%s" db coll ndx // TODO cleanse?

        let getStmtSequence (stmt:sqlite3_stmt) =
            seq {
                while raw.SQLITE_ROW = stmt.step() do
                    let doc = stmt.column_blob(0) |> BinReader.ReadDocument
                    yield doc
            }

        let createIndex info =
            //printfn "createIndex: %A" info
            match getIndexInfo info.db info.coll info.ndx with
            | Some already ->
                //printfn "it already exists: %A" already
                if already.spec<>info.spec then
                    failwith "index already exists with different keys"
                false
            | None ->
                //printfn "did not exist, creating it"
                let baSpec = bson.toBinaryArray info.spec
                let baOptions = bson.toBinaryArray info.options
                use stmt = conn.prepare("INSERT INTO \"indexes\" (dbName,collName,ndxName,spec,options) VALUES (?,?,?,?,?)")
                stmt.bind_text(1,info.db)
                stmt.bind_text(2,info.coll)
                stmt.bind_text(3,info.ndx)
                stmt.bind_blob(4,baSpec)
                stmt.bind_blob(5,baOptions)
                stmt.step_done()
                let collTable = getTableNameForCollection info.db info.coll
                let ndxTable = getTableNameForIndex info.db info.coll info.ndx
                // TODO WITHOUT ROWID ?
                match bson.tryGetValueForKey info.options "unique" with
                | Some (BBoolean true) ->
                    conn.exec(sprintf "CREATE TABLE \"%s\" (k BLOB NOT NULL, doc_rowid int NOT NULL REFERENCES \"%s\"(did) ON DELETE CASCADE, PRIMARY KEY (k))" ndxTable collTable)
                | _ ->
                    conn.exec(sprintf "CREATE TABLE \"%s\" (k BLOB NOT NULL, doc_rowid int NOT NULL REFERENCES \"%s\"(did) ON DELETE CASCADE, PRIMARY KEY (k,doc_rowid))" ndxTable collTable)
                true

        let createCollection db coll options =
            //printfn "createCollection: %s.%s" db coll
            match getCollectionOptions db coll with
            | Some _ ->
                //printfn "collection %s.%s already exists" db coll
                false
            | None ->
                //printfn "collection %s.%s did not exist, creating it" db coll
                let baOptions = bson.toBinaryArray options
                use stmt = conn.prepare("INSERT INTO \"collections\" (dbName,collName,options) VALUES (?,?,?)", db, coll, baOptions)
                stmt.step_done()
                let collTable = getTableNameForCollection db coll
                conn.exec(sprintf "CREATE TABLE \"%s\" (did INTEGER PRIMARY KEY, bson BLOB NOT NULL)" collTable)
                match bson.tryGetValueForKey options "autoIndexId" with
                | Some (BBoolean false) ->
                    ()
                | _ ->
                    let info = 
                        {
                            db = db
                            coll = coll
                            ndx = "_id_"
                            spec = BDocument [| ("_id",BInt32 1) |]
                            options = BDocument [| ("unique",BBoolean true) |]
                        }
                    let created = createIndex info
                    ignore created
                true

        let listCollections() =
            use stmt = conn.prepare("SELECT dbName, collName, options FROM \"collections\" ORDER BY collName ASC")
            let results = ResizeArray<_>()
            while raw.SQLITE_ROW = stmt.step() do
                let dbName = stmt.column_text(0)
                let collName = stmt.column_text(1)
                let options = stmt.column_blob(2) |> BinReader.ReadDocument
                results.Add(dbName,collName,options)
            results.ToArray()

        let listIndexes() =
            use stmt = conn.prepare(sqlSelectIndexes)
            let results = ResizeArray<_>()
            while raw.SQLITE_ROW = stmt.step() do
                results.Add(getIndexFromRow stmt)
            results.ToArray()

        let dropIndex db coll ndxName =
            //printfn "dropping index: %s.%s.%s" db coll ndxName
            match getIndexInfo db coll ndxName with
            | Some _ ->
                //printfn "and yes, it did exist"
                use stmt_ndx = conn.prepare("DELETE FROM \"indexes\" WHERE dbName=? AND collName=? AND ndxName=?")
                stmt_ndx.bind_text(1,db)
                stmt_ndx.bind_text(2,coll)
                stmt_ndx.bind_text(3,ndxName)
                stmt_ndx.step_done()
                let deleted = conn.changes()>0
                // TODO assert deleted is true?
                let ndxTable = getTableNameForIndex db coll ndxName
                conn.exec(sprintf "DROP TABLE \"%s\"" ndxTable)
                deleted
            | None ->
                //printfn "it did not exist"
                false

        let dropCollection db coll =
            //printfn "dropping collection: %s.%s" db coll
            match getCollectionOptions db coll with
            | Some _ ->
                //printfn "it exists, dropping it"
                let indexes = listIndexes() |> Array.filter (fun ndxInfo -> db=ndxInfo.db && coll=ndxInfo.coll)
                Array.iter (fun info ->
                    let deleted = dropIndex info.db info.coll info.ndx
                    ignore deleted
                ) indexes
                let collTable = getTableNameForCollection db coll
                conn.exec(sprintf "DROP TABLE \"%s\"" collTable)
                use stmt=conn.prepare("DELETE FROM \"collections\" WHERE dbName=? AND collName=?")
                stmt.bind_text(1,db)
                stmt.bind_text(2,coll)
                stmt.step_done()
                let deleted = conn.changes()>0
                // TODO assert deleted is true?
                deleted
            | None ->
                //printfn "but it did not exist"
                false

        let renameCollection oldName newName dropTarget =
            let (oldDb,oldColl) = bson.splitname oldName
            let (newDb,newColl) = bson.splitname newName

            // jstests/core/rename8.js seems to think that renaming to/from a system collection is illegal unless
            // that collection is system.users, which is "whitelisted".  for now, we emulate this behavior, even
            // though system.users isn't supported.
            if oldColl<>"system.users" && oldColl.StartsWith("system.") then failwith "renameCollection with a system collection not allowed."
            if newColl<>"system.users" && newColl.StartsWith("system.") then failwith "renameCollection with a system collection not allowed."

            if dropTarget then 
                let deleted = dropCollection newDb newColl
                ignore deleted

            match getCollectionOptions oldDb oldColl with
            | Some _ -> 
                let indexes = listIndexes() |> Array.filter (fun ndxInfo -> oldDb=ndxInfo.db && oldColl=ndxInfo.coll)
                let oldTable = getTableNameForCollection oldDb oldColl
                let newTable = getTableNameForCollection newDb newColl
                let f which =
                    use stmt = conn.prepare(sprintf "UPDATE \"%s\" SET dbName=?, collName=? WHERE dbName=? AND collName=?" which)
                    stmt.bind_text(1, newDb)
                    stmt.bind_text(2, newColl)
                    stmt.bind_text(3, oldDb)
                    stmt.bind_text(4, oldColl)
                    stmt.step_done()
                // f "indexes" // ON UPDATE CASCADE does this
                f "collections"
                conn.exec(sprintf "ALTER TABLE \"%s\" RENAME TO \"%s\"" oldTable newTable)
                Array.iter (fun ndxInfo ->
                    let oldName = getTableNameForIndex oldDb oldColl ndxInfo.ndx
                    let newName = getTableNameForIndex newDb newColl ndxInfo.ndx
                    conn.exec(sprintf "ALTER TABLE \"%s\" RENAME TO \"%s\"" oldName newName)
                ) indexes
                false
            | None -> 
                createCollection newDb newColl (BDocument Array.empty)

        //let fn_close() = conn.close()

        let fn_close() = 
            try
                conn.close()
            with
            | _ ->
                let mutable cur = conn.next_stmt(null)
                while cur<>null do
                    printfn "%s" (cur.sql())
                    cur <- conn.next_stmt(cur)
                reraise()

        let getCollectionSequence tx db coll =
            match getCollectionOptions db coll with
            | Some _ ->
                let collTable = getTableNameForCollection db coll
                if tx then conn.exec("BEGIN TRANSACTION")
                let stmt = conn.prepare(sprintf "SELECT bson FROM \"%s\"" collTable)

                let s = getStmtSequence stmt

                let killFunc() =
                    // TODO would it be possible/helpful to make sure this function
                    // can be safely called more than once?
                    if tx then conn.exec("COMMIT TRANSACTION")
                    stmt.sqlite3_finalize()

                (s, killFunc)
            | None ->
                (Seq.empty, (fun () -> ()))

        let beginWrite db coll = 
            let created = createCollection db coll (BDocument Array.empty)
            ignore created
            let tblCollection = getTableNameForCollection db coll
            let stmt_insert = conn.prepare(sprintf "INSERT INTO \"%s\" (bson) VALUES (?)" tblCollection)
            let stmt_delete = conn.prepare(sprintf "DELETE FROM \"%s\" WHERE rowid=?" tblCollection)
            let stmt_update = conn.prepare(sprintf "UPDATE \"%s\" SET bson=? WHERE rowid=?" tblCollection)
            let indexes = listIndexes() |> Array.filter (fun ndxInfo -> db=ndxInfo.db && coll=ndxInfo.coll)
            let opt_stmt_find_rowid = 
                match Array.tryFind (fun info -> info.ndx="_id_") indexes with
                | Some info ->
                    let tblIndex = getTableNameForIndex db coll "_id_"
                    let stmt_find_rowid = conn.prepare(sprintf "SELECT doc_rowid FROM \"%s\" WHERE k=?" tblIndex)
                    Some stmt_find_rowid
                | None ->
                    None

            let index_stmts = 
                Array.map (fun info->
                    let tbl = getTableNameForIndex db coll info.ndx
                    let stmt_insert = conn.prepare(sprintf "INSERT INTO \"%s\" (k,doc_rowid) VALUES (?,?)" tbl)
                    let stmt_delete = conn.prepare(sprintf "DELETE FROM \"%s\" WHERE doc_rowid=?" tbl)
                    (info,stmt_insert,stmt_delete)
                ) indexes

            let find_rowid id =
                match opt_stmt_find_rowid with
                | Some stmt_find_rowid ->
                    let idk = bson.encodeForIndex id
                    stmt_find_rowid.clear_bindings()
                    stmt_find_rowid.bind_blob(1, idk)
                    let rowid = 
                        if raw.SQLITE_ROW=stmt_find_rowid.step() then
                            stmt_find_rowid.column_int64(0) |> Some
                        else
                            None
                    stmt_find_rowid.reset()
                    rowid
                | None ->
                    None

            let update_indexes doc_rowid newDoc =
                Array.iter (fun (info,stmt_insert:sqlite3_stmt,stmt_delete:sqlite3_stmt) ->
                    stmt_delete.clear_bindings()
                    stmt_delete.bind_int64(1, doc_rowid)
                    stmt_delete.step_done()
                    stmt_delete.reset()

                    let k = encodeIndexKey info newDoc
                    stmt_insert.clear_bindings()
                    stmt_insert.bind_blob(1, k)
                    stmt_insert.bind_int64(2, doc_rowid)
                    try
                        stmt_insert.step_done()
                    with
                    | e ->
                        let msg = conn.errmsg()
                        failwith (sprintf "%s:%A" msg e)
                    if conn.changes()<>1 then failwith "insert failed"
                    stmt_insert.reset()
                ) index_stmts

            let to_bson newDoc =
                // TODO validateKeys here?
                let ba = bson.toBinaryArray newDoc
                if ba.Length > 16*1024*1024 then raise (MongoCode(10329, "document more than 16MB"))
                ba

            let fn_insert newDoc =
                let ba = to_bson newDoc
                stmt_insert.clear_bindings()
                stmt_insert.bind_blob(1, ba)
                stmt_insert.step_done()
                let doc_rowid = conn.last_insert_rowid()
                stmt_insert.reset()
                if conn.changes()<>1 then failwith "insert failed"
                update_indexes doc_rowid newDoc

            let fn_delete id =
                match find_rowid id with
                | Some rowid ->
                    stmt_delete.clear_bindings()
                    stmt_delete.bind_int64(1,rowid)
                    stmt_delete.step_done()
                    stmt_delete.reset()
                    true
                | None ->
                    false

            let fn_update newDoc =
                match bson.tryGetValueForKey newDoc "_id" with
                | Some id ->
                    match find_rowid id with
                    | Some rowid ->
                        let ba = to_bson newDoc
                        stmt_update.clear_bindings()
                        stmt_update.bind_blob(1, ba)
                        stmt_update.bind_int64(2, rowid)
                        stmt_update.step_done()
                        stmt_update.reset()
                        if conn.changes()<>1 then failwith "insert failed"
                        update_indexes rowid newDoc
                    | None ->
                        failwith "update, but does not exist"
                | None ->
                    failwith "cannot update without id"

            let finalize_stmts() =
                match opt_stmt_find_rowid with
                | Some stmt_find_rowid -> stmt_find_rowid.sqlite3_finalize()
                | None -> ()
                stmt_insert.sqlite3_finalize()
                stmt_delete.sqlite3_finalize()
                stmt_update.sqlite3_finalize()
                Array.iter (fun (_,stmt_insert:sqlite3_stmt,stmt_delete:sqlite3_stmt) -> 
                    stmt_insert.sqlite3_finalize()
                    stmt_delete.sqlite3_finalize()
                    ) index_stmts

            let fn_rollback() =
                //printfn "rollback"
                conn.exec("ROLLBACK TRANSACTION")
                finalize_stmts()
                
            let fn_commit() =
                //printfn "commit"
                conn.exec("COMMIT TRANSACTION")
                finalize_stmts()
                
            let fn_select() =
                getCollectionSequence false db coll

            conn.exec("BEGIN TRANSACTION")

            {
                database = db
                collection = db
                insert = fn_insert
                update = fn_update
                delete = fn_delete
                getSelect = fn_select
                commit = fn_commit
                rollback = fn_rollback
            }

        let fn_beginWrite db coll =
            try
                beginWrite db coll
            with
            | e ->
                printfn "%A" e
                reraise()

        let fn_getCollectionSequence db coll =
            getCollectionSequence true db coll

        let fn_listCollections() =
            conn.exec("BEGIN TRANSACTION")
            try
                let a = listCollections()
                conn.exec("COMMIT TRANSACTION")
                a
            with
            | _ ->
                conn.exec("ROLLBACK TRANSACTION")
                reraise()

        let fn_listIndexes() =
            conn.exec("BEGIN TRANSACTION")
            try
                let a = listIndexes()
                conn.exec("COMMIT TRANSACTION")
                a
            with
            | _ ->
                conn.exec("ROLLBACK TRANSACTION")
                reraise()

        let fn_dropIndex db coll ndx =
            conn.exec("BEGIN TRANSACTION")
            try
                let a = dropIndex db coll ndx
                conn.exec("COMMIT TRANSACTION")
                a
            with
            | _ ->
                conn.exec("ROLLBACK TRANSACTION")
                reraise()

        let fn_createCollection db coll =
            conn.exec("BEGIN TRANSACTION")
            try
                let a = createCollection db coll 
                conn.exec("COMMIT TRANSACTION")
                a
            with
            | _ ->
                conn.exec("ROLLBACK TRANSACTION")
                reraise()

        let fn_dropCollection db coll =
            conn.exec("BEGIN TRANSACTION")
            try
                let a = dropCollection db coll 
                conn.exec("COMMIT TRANSACTION")
                a
            with
            | _ ->
                conn.exec("ROLLBACK TRANSACTION")
                reraise()

        let fn_renameCollection oldFullName newFullName dropTarget =
            conn.exec("BEGIN TRANSACTION")
            try
                let a = renameCollection oldFullName newFullName dropTarget
                conn.exec("COMMIT TRANSACTION")
                a
            with
            | _ ->
                conn.exec("ROLLBACK TRANSACTION")
                reraise()

        let fn_clearCollection db coll = 
            conn.exec("BEGIN TRANSACTION")
            try
                let created = 
                    match getCollectionOptions db coll with
                    | Some _ ->
                        let collTable = getTableNameForCollection db coll
                        conn.exec(sprintf "DELETE FROM \"%s\"" collTable)
                        false
                    | None ->
                        createCollection db coll (BDocument Array.empty)
                conn.exec("COMMIT TRANSACTION")
                created
            with
            | _ ->
                conn.exec("ROLLBACK TRANSACTION")
                reraise()

        let fn_dropDatabase db =
            conn.exec("BEGIN TRANSACTION")
            try
                let a = listCollections() |> Array.filter (fun (name,_,_) -> db=name)
                let deleted = Array.isEmpty a |> not
                Array.iter (fun (db,coll,_) ->
                    let deleted = dropCollection db coll
                    ignore deleted
                ) a
                conn.exec("COMMIT TRANSACTION")
                deleted
            with
            | _ ->
                conn.exec("ROLLBACK TRANSACTION")
                reraise()

        let fn_createIndexes a =
            conn.exec("BEGIN TRANSACTION")
            try
                let r =
                    Array.map (fun info ->
                        createIndex info
                    ) a
                conn.exec("COMMIT TRANSACTION")
                r
            with
            | _ ->
                conn.exec("ROLLBACK TRANSACTION")
                reraise()

        // ----------------------------------------------------------------

        {
            close = fn_close
            beginWrite = fn_beginWrite
            listCollections = fn_listCollections
            listIndexes = fn_listIndexes
            getCollectionSequence = fn_getCollectionSequence
            clearCollection = fn_clearCollection
            dropDatabase = fn_dropDatabase
            createIndexes = fn_createIndexes
            dropIndex = fn_dropIndex
            createCollection = fn_createCollection
            dropCollection = fn_dropCollection
            renameCollection = fn_renameCollection
        }

