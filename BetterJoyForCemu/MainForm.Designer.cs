namespace BetterJoyForCemu {
    partial class MainForm {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing) {
            if (disposing && (components != null)) {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent() {
            this.components = new System.ComponentModel.Container();
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MainForm));
            this.console = new System.Windows.Forms.TextBox();
            this.notifyIcon = new System.Windows.Forms.NotifyIcon(this.components);
            this.contextMenu = new System.Windows.Forms.ContextMenuStrip(this.components);
            this.exitToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.version_lbl = new System.Windows.Forms.Label();
            this.donationLink = new System.Windows.Forms.LinkLabel();
            this.conCntrls = new System.Windows.Forms.GroupBox();
            this.loc4 = new System.Windows.Forms.Button();
            this.loc3 = new System.Windows.Forms.Button();
            this.loc2 = new System.Windows.Forms.Button();
            this.loc1 = new System.Windows.Forms.Button();
            this.con4 = new System.Windows.Forms.Button();
            this.con3 = new System.Windows.Forms.Button();
            this.con2 = new System.Windows.Forms.Button();
            this.con1 = new System.Windows.Forms.Button();
            this.btnTip = new System.Windows.Forms.ToolTip(this.components);
            this.groupBox1 = new System.Windows.Forms.GroupBox();
            this.settingsTable = new System.Windows.Forms.TableLayoutPanel();
            this.rightPanel = new System.Windows.Forms.Panel();
            this.settingsApply = new System.Windows.Forms.Button();
            this.btn_enableServiceMode = new System.Windows.Forms.Button();
            this.btn_settings = new System.Windows.Forms.Button();
            this.btn_controllerProfiles = new System.Windows.Forms.Button();
            this.contextMenu.SuspendLayout();
            this.conCntrls.SuspendLayout();
            this.groupBox1.SuspendLayout();
            this.rightPanel.SuspendLayout();
            this.SuspendLayout();
            // 
            // console
            //
            this.console.Location = new System.Drawing.Point(12, 117);
            this.console.Multiline = true;
            this.console.Name = "console";
            this.console.ReadOnly = true;
            this.console.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.console.Size = new System.Drawing.Size(270, 100);
            this.console.TabIndex = 2;
            // 
            // notifyIcon
            // 
            this.notifyIcon.BalloonTipText = "Double click the tray icon to maximise!";
            this.notifyIcon.BalloonTipTitle = "BetterJoy2";
            this.notifyIcon.ContextMenuStrip = this.contextMenu;
            this.notifyIcon.Icon = ((System.Drawing.Icon)(resources.GetObject("notifyIcon.Icon")));
            this.notifyIcon.Text = "BetterJoy2";
            this.notifyIcon.Visible = true;
            this.notifyIcon.MouseDoubleClick += new System.Windows.Forms.MouseEventHandler(this.notifyIcon_MouseDoubleClick);
            // 
            // contextMenu
            // 
            this.contextMenu.ImageScalingSize = new System.Drawing.Size(24, 24);
            this.contextMenu.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.exitToolStripMenuItem});
            this.contextMenu.Name = "contextMenu";
            this.contextMenu.Size = new System.Drawing.Size(94, 26);
            // 
            // exitToolStripMenuItem
            // 
            this.exitToolStripMenuItem.Name = "exitToolStripMenuItem";
            this.exitToolStripMenuItem.Size = new System.Drawing.Size(93, 22);
            this.exitToolStripMenuItem.Text = "Exit";
            this.exitToolStripMenuItem.Click += new System.EventHandler(this.exitToolStripMenuItem_Click);
            // 
            // version_lbl
            // 
            this.version_lbl.AutoSize = true;
            this.version_lbl.Location = new System.Drawing.Point(12, 221);
            this.version_lbl.Name = "version_lbl";
            this.version_lbl.Size = new System.Drawing.Size(28, 13);
            this.version_lbl.TabIndex = 2;
            //
            // donationLink
            //
            this.donationLink.AutoSize = true;
            this.donationLink.Location = new System.Drawing.Point(198, 248);
            this.donationLink.Name = "donationLink";
            this.donationLink.Padding = new System.Windows.Forms.Padding(0, 0, 0, 10);
            this.donationLink.Size = new System.Drawing.Size(42, 23);
            this.donationLink.TabIndex = 5;
            this.donationLink.TabStop = true;
            this.donationLink.Text = "Donate";
            this.donationLink.Visible = false;
            this.donationLink.LinkClicked += new System.Windows.Forms.LinkLabelLinkClickedEventHandler(this.linkLabel1_LinkClicked);
            // 
            // conCntrls
            // 
            this.conCntrls.Controls.Add(this.loc4);
            this.conCntrls.Controls.Add(this.loc3);
            this.conCntrls.Controls.Add(this.loc2);
            this.conCntrls.Controls.Add(this.loc1);
            this.conCntrls.Controls.Add(this.con4);
            this.conCntrls.Controls.Add(this.con3);
            this.conCntrls.Controls.Add(this.con2);
            this.conCntrls.Controls.Add(this.con1);
            this.conCntrls.Location = new System.Drawing.Point(12, 9);
            this.conCntrls.Name = "conCntrls";
            this.conCntrls.Size = new System.Drawing.Size(270, 104);
            this.conCntrls.TabIndex = 0;
            this.conCntrls.TabStop = false;
            this.conCntrls.Text = "Connected Controllers";
            // 
            // loc4
            // 
            this.loc4.Location = new System.Drawing.Point(200, 80);
            this.loc4.Name = "loc4";
            this.loc4.Size = new System.Drawing.Size(58, 20);
            this.loc4.TabIndex = 7;
            this.loc4.Text = "Locate";
            this.loc4.UseVisualStyleBackColor = true;
            // 
            // loc3
            // 
            this.loc3.Location = new System.Drawing.Point(136, 80);
            this.loc3.Name = "loc3";
            this.loc3.Size = new System.Drawing.Size(58, 20);
            this.loc3.TabIndex = 6;
            this.loc3.Text = "Locate";
            this.loc3.UseVisualStyleBackColor = true;
            // 
            // loc2
            // 
            this.loc2.Location = new System.Drawing.Point(72, 80);
            this.loc2.Name = "loc2";
            this.loc2.Size = new System.Drawing.Size(58, 20);
            this.loc2.TabIndex = 5;
            this.loc2.Text = "Locate";
            this.loc2.UseVisualStyleBackColor = true;
            // 
            // loc1
            // 
            this.loc1.Location = new System.Drawing.Point(6, 80);
            this.loc1.Name = "loc1";
            this.loc1.Size = new System.Drawing.Size(58, 20);
            this.loc1.TabIndex = 4;
            this.loc1.Text = "Locate";
            this.loc1.UseVisualStyleBackColor = true;
            // 
            // con4
            // 
            this.con4.BackgroundImage = global::BetterJoyForCemu.Properties.Resources.cross;
            this.con4.BackgroundImageLayout = System.Windows.Forms.ImageLayout.Stretch;
            this.con4.Location = new System.Drawing.Point(200, 20);
            this.con4.Name = "con4";
            this.con4.Size = new System.Drawing.Size(58, 59);
            this.con4.TabIndex = 3;
            this.con4.TabStop = false;
            this.con4.UseVisualStyleBackColor = true;
            // 
            // con3
            // 
            this.con3.BackgroundImage = global::BetterJoyForCemu.Properties.Resources.cross;
            this.con3.BackgroundImageLayout = System.Windows.Forms.ImageLayout.Stretch;
            this.con3.Location = new System.Drawing.Point(136, 20);
            this.con3.Name = "con3";
            this.con3.Size = new System.Drawing.Size(58, 59);
            this.con3.TabIndex = 2;
            this.con3.TabStop = false;
            this.con3.UseVisualStyleBackColor = true;
            // 
            // con2
            // 
            this.con2.BackgroundImage = global::BetterJoyForCemu.Properties.Resources.cross;
            this.con2.BackgroundImageLayout = System.Windows.Forms.ImageLayout.Stretch;
            this.con2.Location = new System.Drawing.Point(72, 20);
            this.con2.Name = "con2";
            this.con2.Size = new System.Drawing.Size(58, 59);
            this.con2.TabIndex = 1;
            this.con2.TabStop = false;
            this.con2.UseVisualStyleBackColor = true;
            // 
            // con1
            // 
            this.con1.BackgroundImage = global::BetterJoyForCemu.Properties.Resources.cross;
            this.con1.BackgroundImageLayout = System.Windows.Forms.ImageLayout.Stretch;
            this.con1.Location = new System.Drawing.Point(6, 20);
            this.con1.Name = "con1";
            this.con1.Size = new System.Drawing.Size(58, 59);
            this.con1.TabIndex = 0;
            this.con1.TabStop = false;
            this.con1.UseVisualStyleBackColor = true;
            //
            // groupBox1
            // 
            this.groupBox1.Controls.Add(this.settingsTable);
            this.groupBox1.Location = new System.Drawing.Point(3, 11);
            this.groupBox1.Margin = new System.Windows.Forms.Padding(2);
            this.groupBox1.Name = "groupBox1";
            this.groupBox1.Padding = new System.Windows.Forms.Padding(2);
            this.groupBox1.Size = new System.Drawing.Size(304, 242);
            this.groupBox1.TabIndex = 9;
            this.groupBox1.TabStop = false;
            this.groupBox1.Text = "Config";
            // 
            // settingsTable
            // 
            this.settingsTable.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.settingsTable.AutoScroll = true;
            this.settingsTable.ColumnCount = 2;
            this.settingsTable.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 58.90411F));
            this.settingsTable.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 41.09589F));
            this.settingsTable.Location = new System.Drawing.Point(4, 17);
            this.settingsTable.Margin = new System.Windows.Forms.Padding(2);
            this.settingsTable.Name = "settingsTable";
            this.settingsTable.RowCount = 1;
            this.settingsTable.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.settingsTable.Size = new System.Drawing.Size(300, 219);
            this.settingsTable.TabIndex = 1;
            // 
            // rightPanel
            //
            this.rightPanel.Controls.Add(this.settingsApply);
            this.rightPanel.Controls.Add(this.btn_enableServiceMode);
            this.rightPanel.Controls.Add(this.groupBox1);
            this.rightPanel.Location = new System.Drawing.Point(289, 0);
            this.rightPanel.Margin = new System.Windows.Forms.Padding(2, 2, 12, 2);
            this.rightPanel.Name = "rightPanel";
            this.rightPanel.Size = new System.Drawing.Size(312, 282);
            this.rightPanel.TabIndex = 11;
            this.rightPanel.Visible = false;
            //
            // settingsApply
            //
            this.settingsApply.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.settingsApply.Font = new System.Drawing.Font("Microsoft Sans Serif", 8F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.settingsApply.Location = new System.Drawing.Point(246, 257);
            this.settingsApply.Margin = new System.Windows.Forms.Padding(2);
            this.settingsApply.Name = "settingsApply";
            this.settingsApply.Size = new System.Drawing.Size(61, 21);
            this.settingsApply.TabIndex = 10;
            this.settingsApply.Text = "Apply";
            this.settingsApply.UseVisualStyleBackColor = true;
            this.settingsApply.Click += new System.EventHandler(this.settingsApply_Click);
            //
            // btn_enableServiceMode
            //
            this.btn_enableServiceMode.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.btn_enableServiceMode.Font = new System.Drawing.Font("Microsoft Sans Serif", 8F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.btn_enableServiceMode.Location = new System.Drawing.Point(3, 257);
            this.btn_enableServiceMode.Margin = new System.Windows.Forms.Padding(2);
            this.btn_enableServiceMode.Name = "btn_enableServiceMode";
            this.btn_enableServiceMode.Size = new System.Drawing.Size(150, 21);
            this.btn_enableServiceMode.TabIndex = 12;
            this.btn_enableServiceMode.Text = "Sync Config with Service";
            this.btn_enableServiceMode.UseVisualStyleBackColor = true;
            this.btn_enableServiceMode.Click += new System.EventHandler(this.btn_enableServiceMode_Click);
            //
            // btn_settings
            //
            this.btn_settings.BackgroundImage = global::BetterJoyForCemu.Properties.Resources.gear;
            this.btn_settings.BackgroundImageLayout = System.Windows.Forms.ImageLayout.Zoom;
            this.btn_settings.FlatAppearance.BorderSize = 0;
            this.btn_settings.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btn_settings.Location = new System.Drawing.Point(262, 221);
            this.btn_settings.Margin = new System.Windows.Forms.Padding(3, 3, 12, 3);
            this.btn_settings.Name = "btn_settings";
            this.btn_settings.Size = new System.Drawing.Size(19, 19);
            this.btn_settings.TabIndex = 14;
            this.btn_settings.UseVisualStyleBackColor = true;
            this.btnTip.SetToolTip(this.btn_settings, "Settings");
            this.btn_settings.Click += new System.EventHandler(this.btn_settings_Click);
            //
            // btn_controllerProfiles
            //
            this.btn_controllerProfiles.BackgroundImage = global::BetterJoyForCemu.Properties.Resources.betterjoyforcemu_icon.ToBitmap();
            this.btn_controllerProfiles.BackgroundImageLayout = System.Windows.Forms.ImageLayout.Zoom;
            this.btn_controllerProfiles.FlatAppearance.BorderSize = 0;
            this.btn_controllerProfiles.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btn_controllerProfiles.Location = new System.Drawing.Point(237, 221);
            this.btn_controllerProfiles.Margin = new System.Windows.Forms.Padding(3);
            this.btn_controllerProfiles.Name = "btn_controllerProfiles";
            this.btn_controllerProfiles.Size = new System.Drawing.Size(19, 19);
            this.btn_controllerProfiles.TabIndex = 15;
            this.btn_controllerProfiles.UseVisualStyleBackColor = true;
            this.btnTip.SetToolTip(this.btn_controllerProfiles, "Controller Profiles");
            this.btn_controllerProfiles.Click += new System.EventHandler(this.btn_reassign_open_Click);
            // 
            // MainForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.AutoSize = true;
            this.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.ClientSize = new System.Drawing.Size(633, 284);
            this.Controls.Add(this.btn_controllerProfiles);
            this.Controls.Add(this.btn_settings);
            this.Controls.Add(this.rightPanel);
            this.Controls.Add(this.conCntrls);
            this.Controls.Add(this.donationLink);
            this.Controls.Add(this.version_lbl);
            this.Controls.Add(this.console);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.MaximizeBox = false;
            this.Name = "MainForm";
            this.Text = "BetterJoy2";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.MainForm_FormClosing);
            this.Load += new System.EventHandler(this.MainForm_Load);
            this.Resize += new System.EventHandler(this.MainForm_Resize);
            this.contextMenu.ResumeLayout(false);
            this.conCntrls.ResumeLayout(false);
            this.groupBox1.ResumeLayout(false);
            this.rightPanel.ResumeLayout(false);
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        public System.Windows.Forms.TextBox console;
        public System.Windows.Forms.NotifyIcon notifyIcon;
        private System.Windows.Forms.Label version_lbl;
        private System.Windows.Forms.ContextMenuStrip contextMenu;
        private System.Windows.Forms.ToolStripMenuItem exitToolStripMenuItem;
        private System.Windows.Forms.LinkLabel donationLink;
        private System.Windows.Forms.GroupBox conCntrls;
        private System.Windows.Forms.Button con1;
        private System.Windows.Forms.Button con4;
        private System.Windows.Forms.Button con3;
        private System.Windows.Forms.Button con2;
        private System.Windows.Forms.Button loc4;
        private System.Windows.Forms.Button loc3;
        private System.Windows.Forms.Button loc2;
        private System.Windows.Forms.Button loc1;
        private System.Windows.Forms.ToolTip btnTip;
        private System.Windows.Forms.GroupBox groupBox1;
        private System.Windows.Forms.TableLayoutPanel settingsTable;
        private System.Windows.Forms.Panel rightPanel;
        private System.Windows.Forms.Button settingsApply;
        private System.Windows.Forms.Button btn_enableServiceMode;
        private System.Windows.Forms.Button btn_settings;
        private System.Windows.Forms.Button btn_controllerProfiles;
    }
}
