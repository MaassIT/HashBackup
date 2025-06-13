#!/usr/bin/env -S /opt/venv/bin/python -u
import os
import hashlib
import csv
import xattr
import time
import threading
import queue
import curses
import sys
import fcntl
import argparse
import mmap
import traceback

# ToDo: Wiederkehrende Fehler checken
# ToDo: Beenden wenn alle Worker fertig sind
# ToDo: Mit LVM Snapshots arbeiten
# ToDo: Beim Upload nochmal kontrollieren ob Datei sich verändert hat

from config import Settings, ArgParserAddArguments
from datetime import datetime
from backend.storage_backend_factory import create_storage_backend

# Argumente parsen
parser = argparse.ArgumentParser(description="Backup-Skript mit Azure Blob Storage-Unterstützung.")
parser.add_argument("-c", "--config", dest="config", required=True, help="Pfad zur Konfigurationsdatei")
ArgParserAddArguments(parser=parser)
args = parser.parse_args()

# Konfigurationsdatei einbinden
print(f"Starte Backup mit Konfiguration '{args.config}'")
Settings.load(args)

# Einstellen des Backends
storage_backend = create_storage_backend()

ATTR_NAME_MD5_HASH_VALUE = "user.md5_hash_value"
ATTR_NAME_MD5_HASH_MTIME = "user.md5_hash_mtime"
ATTR_NAME_BACKUP_MTIME = f"user.{Settings.DEFAULT.JOB_NAME}_backup_mtime"

# Backup LOCK setzen, damit das Script nur einmal läuft
lock_file = open(Settings.DEFAULT.LOCK_FILE, "w")

try:
    fcntl.flock(lock_file, fcntl.LOCK_EX | fcntl.LOCK_NB)
except BlockingIOError:
    print("Skript läuft bereits. Beende...")
    sys.exit(1)

upload_queue = queue.Queue()
remaining_files = 0
remaining_size = 0
saved_files = 0
saved_size = 0
files_indized = 0
lock = threading.Lock()
shutdown_flag = threading.Event()
worker_status = ["Leerlauf" for _ in range(Settings.DEFAULT.PARALLEL_UPLOADS)]
indexing_active = True
container_client = None
hashes_to_upload = []

if Settings.DEFAULT.DRY_RUN:
    print("\n*** WARNUNG: DRY RUN AKTIVIERT! KEINE DATEIEN WERDEN HOCHGELADEN ODER VERÄNDERT! ***\n")

def calculate_hash(file_path, hash_algorithm="md5"):
    hash_func = hashlib.new(hash_algorithm)
    with open(file_path, "rb") as f:
        with mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ) as mm:
            hash_func.update(mm)
    return hash_func.hexdigest()

# Funktion zur Verarbeitung der Upload-Warteschlange
def upload_worker(worker_id):
    global remaining_files, remaining_size, saved_files, saved_size
    while not shutdown_flag.is_set():
        try:
            file_path, blob_name, file_hash, upload_try = upload_queue.get(timeout=1)
            if file_path is None:
                break
            file_size = os.path.getsize(file_path)
            worker_status[worker_id] = f"Hochladen: {os.path.basename(file_path)} ({blob_name}) - {file_size / 1024 / 1024:.2f} MB"
            success = storage_backend.upload_to_destination(file_path, blob_name, file_hash)
            if success:
                xattr.setxattr(file_path, ATTR_NAME_BACKUP_MTIME, str(os.path.getmtime(file_path)).encode())
                with lock:
                    remaining_files -= 1
                    remaining_size -= file_size
                    saved_files += 1
                    saved_size += file_size
            elif upload_try < Settings.DEFAULT.MAX_RETRIES:
                time.sleep(Settings.DEFAULT.RETRY_DELAY)
                upload_queue.put((file_path, blob_name, file_hash, upload_try + 1))
            else:
                raise RuntimeError(f"Datei {file_path} konnte nicht hochgeladen werden. Versuche beendet")
            worker_status[worker_id] = "Leerlauf"
            upload_queue.task_done()
        except queue.Empty:
            continue  # Falls die Queue leer ist, erneut versuchen
        except Exception as e:
            print(f"Fehler im Upload-Worker {worker_id}: {e}")
            traceback.print_exc()  # Gibt den gesamten Traceback aus
            shutdown_flag.set()  # Setze das Abbruch-Flag, falls ein Fehler auftritt
            break

# Funktion zur Anzeige der interaktiven Fortschrittsanzeige
def interactive_display(stdscr):
    global files_indized
    curses.curs_set(0)
    stdscr.nodelay(True)  # Damit getch() nicht blockiert

    while not shutdown_flag.is_set():
        # Bildschirmausgabe leeren
        stdscr.clear()

        # Werte anzeigen
        with lock:
            stdscr.addstr(0, 0, f"Indizierte Dateien: {files_indized}")
            stdscr.addstr(1, 0, f"Verbleibende Dateien: {remaining_files}")
            stdscr.addstr(2, 0, f"Verbleibende Grösse: {remaining_size / 1024 / 1024:.2f} MB")
            stdscr.addstr(3, 0, f"Indexing läuft: {'Ja' if indexing_active else 'Nein'}")

            for i, status in enumerate(worker_status):
                stdscr.addstr(5 + i, 0, f"Worker {i + 1}: {status}")

        stdscr.refresh()

        # Auf Benutzereingabe prüfen (z.B. 'q' = Abbruch)
        try:
            c = stdscr.getch()
            if c == ord('q'): # User will abbrechen
                shutdown_flag.set()
                return 
        except:
            pass

        time.sleep(0.2)

        # Abbruchbedingung, wenn Indexing beendet und alle Worker Leerlauf:
        if not indexing_active and all(s == "Leerlauf" for s in worker_status):
            return

# Funktion zum Hochladen der Metadaten-Datei
def upload_backup_metadata(metadata_file):
    if Settings.DEFAULT.DRY_RUN:
        print(f"[DRY RUN] Würde die Metadaten-Datei {metadata_file} hochladen.")
        return
    
    now = datetime.now()
    year = now.strftime("%Y")
    month = now.strftime("%m")
    timestamp = now.strftime("%Y-%m-%d_%H-%M-%S")
    
    metadata_blob_name = f"metadata/{Settings.DEFAULT.JOB_NAME}/{year}/{month}/backup_{timestamp}.csv"
    storage_backend.upload_to_destination(metadata_file, metadata_blob_name, calculate_hash(metadata_file))
    print(f"Backup-Metadaten hochgeladen nach {metadata_blob_name}")

# Backup-Metadaten erfassen und Dateien hochladen
def generate_backup(source_folder, metadata_file):
    global files_indized, indexing_active, remaining_files, remaining_size

    try:
        print("generate_backup gestartet:", source_folder, "- Safe Mode:", Settings.DEFAULT.SAFE_MODE)
        if Settings.DEFAULT.SAFE_MODE:
            print("Lade Hash-Liste vom Backupziel zwecks vergleich (SAFE_MODE)")
            hashes = storage_backend.fetch_hashes()
        
        with open(metadata_file, mode="w", newline="") as file:
            
            file.write("Backup Konfiguration:\n")
            file.writelines(line + "\n" for line in Settings.CONFIG_DOKU)  # Liest und schreibt den gesamten Inhalt
            file.write("EOF\n\n")

            writer = csv.writer(file)
            writer.writerow(["Filename", "Hash", "Extension", "Size", "Modified Time", "InQueue"])
            
            current_dir = ""

            for root, _, files in os.walk(source_folder):
                for filename in files:
                    if shutdown_flag.is_set():
                        return
                    if filename in Settings.DEFAULT.IGNORED_FILES:
                        continue  # Datei ignorieren
                    with lock:
                        files_indized += 1
                    
                    file_path = os.path.join(root, filename)
                    file_extension = os.path.splitext(filename)[1]
                    file_size = os.path.getsize(file_path)
                    file_mtime = os.path.getmtime(file_path)  # Änderungstimestamp
                    
                    upload_required = False
                    
                    file_hash_mtime = xattr.getxattr(file_path, ATTR_NAME_MD5_HASH_MTIME).decode() if ATTR_NAME_MD5_HASH_MTIME in xattr.listxattr(file_path) else "0"
                    file_hash = xattr.getxattr(file_path, ATTR_NAME_MD5_HASH_VALUE).decode() if ATTR_NAME_MD5_HASH_VALUE in xattr.listxattr(file_path) else None

                    try:
                        current_attr_mtime = float(xattr.getxattr(file_path, ATTR_NAME_BACKUP_MTIME).decode() if ATTR_NAME_BACKUP_MTIME in xattr.listxattr(file_path) else "0")
                    except ValueError:
                        current_attr_mtime = 0

                    if float(file_hash_mtime) != file_mtime or file_hash is None:
                        file_hash = calculate_hash(file_path, "md5") if file_size > 0 else "0"
                        xattr.setxattr(file_path, ATTR_NAME_MD5_HASH_VALUE, file_hash.encode())  # Hash als Datei-Attribut setzen
                        xattr.setxattr(file_path, ATTR_NAME_MD5_HASH_MTIME, str(os.path.getmtime(file_path)).encode())  # mtime speichern

                    if Settings.DEFAULT.SAFE_MODE:
                        if file_hash not in hashes or hashes[file_hash] != file_size:
                            upload_required = True
                        if not upload_required and float(file_hash_mtime) != file_mtime:
                            print(f"Korrigiere Sicherungsstatus von {filename}.")
                            xattr.setxattr(file_path, ATTR_NAME_BACKUP_MTIME, str(file_mtime).encode())
                    elif float(current_attr_mtime) != file_mtime:
                        upload_required = True

                    if upload_required and file_size > 0 and file_hash not in hashes_to_upload:
                        # Erzeuge den dynamischen Verzeichnispfad basierend auf der gewünschten Tiefe
                        dest_file_path = f"{'/'.join(file_hash[:Settings.DEFAULT.TARGET_DIR_DEPTH])}/{file_hash}{file_extension}"
                        xattr.removexattr(file_path, ATTR_NAME_BACKUP_MTIME) if ATTR_NAME_BACKUP_MTIME in xattr.listxattr(file_path) else None # mtime löschen, da upload erforderlich
                        remaining_files += 1
                        remaining_size += file_size
                        upload_queue.put((file_path, dest_file_path, file_hash, 0))
                        hashes_to_upload.append(file_hash)
                    
                    if root != current_dir:
                        file.write(f"dir >> {root}\n")
                        current_dir = root

                    writer.writerow([filename, file_hash, file_extension, file_size, file_mtime, "x" if upload_required and file_size > 0 else ""])
        
        print(f"Indizierung abgeschlossen, {files_indized} Dateien überprüft")
        print(f"Es müssen noch {remaining_files} Dateien mit {remaining_size / 1024 / 1024:.2f} MB hochzuladen.")
        if not shutdown_flag.is_set():
            upload_backup_metadata(metadata_file)
    except Exception as e:
        print(f"FEHLER in generate_backup: {e}")
        traceback.print_exc()  # Gibt den gesamten Traceback aus
        shutdown_flag.set()
    with lock:
        indexing_active = False

# Upload-Threads starten
threads = []
index_thread = {}

for i in range(Settings.DEFAULT.PARALLEL_UPLOADS):
    t = threading.Thread(target=upload_worker, args=(i,))
    t.start()
    threads.append(t)


try:
    # Backup-Metadaten generieren und dateien hochladen
    index_thread = threading.Thread(
        target=generate_backup,
        args=(Settings.DEFAULT.SOURCE_FOLDER, Settings.DEFAULT.BACKUP_METADATA_FILE)
    )
    index_thread.start()

    # Starte `curses` in einem separaten Thread
    if Settings.DEFAULT.INTERACTIVE_MODE:
        curses.wrapper(interactive_display)

    # Warten, bis die Indexierung fertig ist
    index_thread.join()

    # Warten, bis die Warteschlange leer ist
    upload_queue.join()

    print(f"Backup abgeschlossen: {saved_files} Dateien gesichert mit {saved_size / 1024 / 1024:.2f} MB.")

except KeyboardInterrupt:
    print("Benutzerabbruch erkannt! Beende Threads...")
    shutdown_flag.set()

except Exception as e:
    print(f"Fehler aufgetreten: {e}")
    shutdown_flag.set()

finally:
    index_thread.join(timeout=10)
    upload_queue.join()
    # Falls curses aktiv ist, beenden
    if Settings.DEFAULT.INTERACTIVE_MODE:
        try:
            if curses.isendwin() == False:  # Prüfen, ob curses noch aktiv ist
                curses.endwin()
        except curses.error:
            pass  # Falls curses bereits beendet ist, ignoriere den Fehler

    # Beende Threads durch Einfügen von `None`
    for _ in threads:
        upload_queue.put((None, None, None, None))
    for t in threads:
        t.join()
    
lock_file.close()
os.remove(Settings.DEFAULT.LOCK_FILE)

print("Programm beendet.")